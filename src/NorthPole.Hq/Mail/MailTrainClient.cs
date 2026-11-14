using System.Collections.Concurrent;
using NorthPole.Contracts;
using NorthPole.Messaging;

namespace NorthPole.Hq.Mail;

public sealed record TripResult(bool Arrived, string? Reason);

/// <summary>
/// HQ's side of the conversation with the signal box: send a letter, follow its progress,
/// and tell the train when Santa has finished so it can go home.
/// </summary>
public sealed class MailTrainClient
{
    static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(8);
    static readonly TimeSpan TripTimeout = TimeSpan.FromMinutes(4);

    readonly MqttLink _mqtt;
    readonly ILogger<MailTrainClient> _log;
    readonly ConcurrentDictionary<Guid, Trip> _trips = new();

    public MailTrainClient(MqttLink mqtt, ILogger<MailTrainClient> log)
    {
        _mqtt = mqtt;
        _log = log;
        mqtt.On<MailProgress>(Topics.Progress, OnProgress);
        mqtt.On<TrainState>(Topics.TrainState, state =>
        {
            State = state;
            return Task.CompletedTask;
        });
    }

    /// <summary>Latest state the signal box published (retained, so we get it as soon as we connect).</summary>
    public TrainState? State { get; private set; }

    /// <summary>
    /// Worth sending a letter? Only a clear "no" from the signal box (hub asleep, or its last will saying it went
    /// offline) skips the train. Silence is handled by the accept timeout instead.
    /// </summary>
    public bool TrainLooksOnline => _mqtt.IsConnected && State is not { HubConnected: false };

    /// <summary>
    /// Sends the letter on the train and waits until it reaches the North Pole (or doesn't).
    /// <paramref name="onProgress"/> fires for every stage so the child's screen can follow along.
    /// </summary>
    public async Task<TripResult> CarryAsync(Guid wishId, string childName, Func<MailProgress, Task> onProgress, CancellationToken ct)
    {
        if (!TrainLooksOnline)
            return new(false, State is null ? "The signal box isn't answering" : "The train is asleep");

        var trip = new Trip(onProgress);
        _trips[wishId] = trip;
        try
        {
            await _mqtt.PublishAsync(Topics.Dispatch, new MailDispatch(wishId, childName, DateTimeOffset.Now), ct: ct);

            if (await Task.WhenAny(trip.Accepted.Task, Task.Delay(AcceptTimeout, ct)) != trip.Accepted.Task)
                return new(false, "The signal box didn't answer");

            if (await Task.WhenAny(trip.Finished.Task, Task.Delay(TripTimeout, ct)) != trip.Finished.Task)
                return new(false, "The train took too long");

            return await trip.Finished.Task;
        }
        finally
        {
            // Keep listening for Returning/Home updates for a while, but stop waiting.
            _ = Task.Delay(TimeSpan.FromMinutes(5), CancellationToken.None).ContinueWith(_ => _trips.TryRemove(wishId, out var _));
        }
    }

    public Task SendHomeAsync(Guid wishId) => _mqtt.PublishAsync(Topics.ReturnHome, new ReturnHome(wishId));

    public Task<bool> SendCommandAsync(TrainCommandKind kind) => _mqtt.PublishAsync(Topics.Command, new TrainCommand(kind));

    async Task OnProgress(MailProgress p)
    {
        if (!_trips.TryGetValue(p.WishId, out var trip)) return;

        switch (p.Stage)
        {
            case MailStage.Accepted:
                trip.Accepted.TrySetResult();
                break;
            case MailStage.Arrived:
                trip.Accepted.TrySetResult();
                trip.Finished.TrySetResult(new(true, null));
                break;
            case MailStage.Rejected or MailStage.Failed:
                trip.Accepted.TrySetResult();
                trip.Finished.TrySetResult(new(false, p.Detail));
                break;
        }

        try { await trip.OnProgress(p); }
        catch (Exception ex) { _log.LogWarning(ex, "Progress handler failed"); }
    }

    sealed class Trip(Func<MailProgress, Task> onProgress)
    {
        public Func<MailProgress, Task> OnProgress { get; } = onProgress;
        public TaskCompletionSource Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<TripResult> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
