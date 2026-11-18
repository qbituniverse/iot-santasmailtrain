using System.Threading.Channels;
using Microsoft.Extensions.Options;
using NorthPole.Contracts;
using NorthPole.Hq.Santa;
using NorthPole.Hq.Wishes;

namespace NorthPole.Hq.Mail;

/// <summary>
/// The journey of one letter:
///   mailbox → elves read it (Claude, in parallel with the train ride) → mail train to the North Pole
///   → Santa "reads" it → his reply is spoken at the station and appears on the child's phone → train goes home.
/// If the train is off or stuck, Rudolph delivers the letter instead: the magic never breaks.
/// </summary>
public sealed class WishPipeline(
    WishStore store,
    FamilyService family,
    ISantaBrain brain,
    OfflineSantaBrain offlineBrain,
    SantaVoice voice,
    MailTrainClient train,
    IOptions<HqOptions> options,
    ILogger<WishPipeline> log) : BackgroundService
{
    readonly Channel<Guid> _incoming = Channel.CreateUnbounded<Guid>();
    readonly SemaphoreSlim _parallel = new(3);

    public void Enqueue(Guid wishId) => _incoming.Writer.TryWrite(wishId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Letters that were mid-journey when the power went out get another go.
        var today = DateOnly.FromDateTime(DateTime.Now);
        foreach (var id in store.Query(ws => ws
                     .Where(w => w.Day == today && w.Status is not (WishStatus.Answered or WishStatus.Failed))
                     .Select(w => w.Id)))
        {
            log.LogInformation("Resuming unfinished wish {Wish}", id);
            Enqueue(id);
        }

        await foreach (var id in _incoming.Reader.ReadAllAsync(stoppingToken))
        {
            await _parallel.WaitAsync(stoppingToken);
            _ = Task.Run(async () =>
            {
                try { await ProcessAsync(id, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    log.LogError(ex, "Wish {Wish} failed", id);
                    store.Update(id, w =>
                    {
                        w.Status = WishStatus.Failed;
                        w.AddEvent("oops", "Oh no, a snowstorm at the North Pole! Ask a grown-up to check the elves' dashboard.");
                    });
                }
                finally { _parallel.Release(); }
            }, CancellationToken.None);
        }
    }

    async Task ProcessAsync(Guid id, CancellationToken ct)
    {
        var o = options.Value;
        var letterInfo = store.Read(id, w => new { w.ChildId, w.ChildName, w.Day, w.Text, w.PhotoFile })
                         ?? throw new InvalidOperationException($"Wish {id} vanished");
        var child = family.FindById(letterInfo.ChildId) ?? new ChildOptions { Id = letterInfo.ChildId, Name = letterInfo.ChildName };

        var earlier = store.Query(ws => ws
            .Where(w => w.ChildId == child.Id && w.Id != id && w.Status == WishStatus.Answered)
            .OrderByDescending(w => w.CreatedAt)
            .Take(o.Santa.RememberWishes)
            .OrderBy(w => w.CreatedAt)
            .Select(w => new PastWish(w.Day, w.Text, w.Reading?.SantaReply)));

        byte[]? photo = null;
        string? photoType = null;
        if (letterInfo.PhotoFile is { } photoFile)
        {
            var path = Path.Combine(Path.GetFullPath(o.DataDirectory), "photos", photoFile);
            if (File.Exists(path))
            {
                photo = await File.ReadAllBytesAsync(path, ct);
                photoType = photoFile.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
            }
        }

        var letter = new WishLetter(child, letterInfo.Day, letterInfo.Text, photo, photoType, earlier);

        store.Update(id, w =>
        {
            w.Status = WishStatus.Reading;
            w.AddEvent("elves", "The elves are opening your letter...");
        });

        // Claude reads the letter while the train is travelling, so Santa is ready the moment it arrives.
        var readingTask = ReadWithFallbackAsync(letter, ct);
        var tripTask = train.CarryAsync(id, child.Name, p => OnTrainProgress(id, p), ct);

        var answer = await readingTask;
        store.Update(id, w =>
        {
            w.Reading = answer.Reading;
            w.Brain = answer.Brain;
            w.Usage = answer.Usage;
            w.Bought = answer.Reading.GiftIdeas.Select(_ => false).ToList();
        });
        var voiceTask = voice.SynthesizeAsync(id, answer.Reading.SantaReply, ct);

        var trip = await tripTask;
        if (!trip.Arrived)
        {
            log.LogInformation("Train unavailable for {Wish} ({Reason}): Rudolph takes it", id, trip.Reason);
            store.Update(id, w =>
            {
                w.DeliveredByReindeer = true;
                w.AddEvent("reindeer", "The mail train is snowed in, so Rudolph flew your letter to the North Pole instead!");
            });
        }

        store.Update(id, w =>
        {
            w.Status = WishStatus.AtTheNorthPole;
            w.AddEvent("santa", "Santa is reading your letter...");
        });

        var voiceFile = await voiceTask;
        await Task.Delay(TimeSpan.FromSeconds(2), ct); // a moment of suspense

        store.Update(id, w =>
        {
            w.VoiceFile = voiceFile;
            w.Status = WishStatus.Answered;
            w.AddEvent("answered", "Santa wrote back!");
        });

        await voice.PlayOnStationAsync(voiceFile, $"{child.Name}'s letter", ct);
        if (trip.Arrived) await train.SendHomeAsync(id);
    }

    async Task<SantaAnswer> ReadWithFallbackAsync(WishLetter letter, CancellationToken ct)
    {
        try
        {
            return await brain.ReadAsync(letter, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Claude couldn't read the letter, using the offline Santa");
            return await offlineBrain.ReadAsync(letter, ct);
        }
    }

    Task OnTrainProgress(Guid id, MailProgress p)
    {
        store.Update(id, w =>
        {
            switch (p.Stage)
            {
                case MailStage.Accepted when p.Detail is not null:
                    w.AddEvent("queued", "The mail train is out with another letter. Yours is next!");
                    break;
                case MailStage.Loading:
                    w.AddEvent("loading", "Loading your letter onto the mail train");
                    break;
                case MailStage.Departed:
                    w.Status = WishStatus.OnTheTrain;
                    w.AddEvent("departed", "Choo choo! The mail train is on its way to the North Pole");
                    break;
                case MailStage.Arrived:
                    w.AddEvent("arrived", "The mail train arrived at the North Pole!");
                    break;
            }
        });
        return Task.CompletedTask;
    }
}
