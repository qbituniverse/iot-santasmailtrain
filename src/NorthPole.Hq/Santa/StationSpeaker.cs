using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using static NorthPole.Hq.Processes;

namespace NorthPole.Hq.Santa;

/// <summary>What the dashboard shows about Santa's speakers.</summary>
/// <param name="Card">The sound card Santa plays on, by its ALSA id (Voice:SoundCard); null for the Pi's default output.</param>
/// <param name="Name">The card's own name, such as "Generic USB Audio", while it's plugged in.</param>
/// <param name="Present">Whether the card is plugged in; null when no card is named, so there's nothing to watch.</param>
/// <param name="PluggedInSince">Since when HQ has seen it plugged in, this time round.</param>
/// <param name="Volume">The card's volume, 0 to 100 as the ear hears it (amixer -M), when it has a volume control.</param>
/// <param name="Muted">The card's own mute switch is off.</param>
/// <param name="Speaking">What Santa is saying right now, if anything.</param>
/// <param name="LastSpoke">When Santa last finished speaking on it.</param>
/// <param name="Problem">Why the last try to play went wrong, until one works.</param>
public sealed record SpeakerState(string? Card, string? Name, bool? Present, DateTimeOffset? PluggedInSince, int? Volume,
    bool Muted, string? Speaking, DateTimeOffset? LastSpoke, string? Problem);

/// <summary>
/// Santa's speakers at the North Pole station: small speakers on a USB sound card on the Pi, named in Voice:SoundCard.
/// Plays Santa on that card, looks every few seconds whether it's plugged in and reads its volume, and sets the volume
/// from the dashboard, kept in the data directory and put back whenever the card is plugged in again. The speakers' own
/// power and knob are out of its sight. With no card named, Santa plays on the Pi's default output.
/// </summary>
public sealed partial class StationSpeaker(IOptions<HqOptions> options, ILogger<StationSpeaker> log) : BackgroundService
{
    static readonly TimeSpan Look = TimeSpan.FromSeconds(5);
    const string Cards = "/proc/asound/cards";

    readonly VoiceOptions _o = options.Value.Voice;
    readonly string _file = Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "speaker.json");
    readonly Lock _lock = new();
    string? _name, _speaking, _problem;
    bool? _present;
    long? _pluggedAt; // a Stopwatch timestamp, so the clock jumping at boot (the Pi has no battery clock) can't upset it
    int? _volume;
    bool _muted;
    DateTimeOffset? _lastSpoke;
    int? _chosen; // the volume an elf chose on the dashboard

    string? Card => string.IsNullOrWhiteSpace(_o.SoundCard) ? null : _o.SoundCard.Trim();

    public SpeakerState State
    {
        get
        {
            lock (_lock)
                return new(Card, _name, _present, _pluggedAt is { } t ? DateTimeOffset.Now - Stopwatch.GetElapsedTime(t) : null,
                    _volume, _muted, _speaking, _lastSpoke, _problem);
        }
    }

    /// <summary>Where Santa plays, in words: "the speakers (Generic USB Audio)", or the Pi's own output.</summary>
    public string Where
    {
        get
        {
            lock (_lock) return Card is null ? "the Pi's own output" : $"the speakers ({_name ?? Card})";
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _chosen = LoadVolume();
        if (Card is null)
        {
            log.LogInformation("No Voice:SoundCard named: Santa plays on the Pi's default output");
            return;
        }
        using var timer = new PeriodicTimer(Look);
        do await LookAsync(stoppingToken);
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Is the card there, and how loud is it? Says so in the log when it comes or goes.</summary>
    async Task LookAsync(CancellationToken ct)
    {
        var name = FindCard(Card!);
        bool? was;
        lock (_lock)
        {
            was = _present;
            _present = name is not null;
            _name = name;
            if (name is null) (_pluggedAt, _volume, _muted) = (null, null, false);
            else if (was != true) _pluggedAt = Stopwatch.GetTimestamp();
        }
        if (name is null)
        {
            if (was != false)
                log.LogWarning("Santa's speakers aren't plugged in: no sound card {Card}. He can't be heard at the station until they are", Card);
            return;
        }
        if (was != true)
        {
            log.LogInformation("Santa's speakers are plugged in: {Name} (card {Card})", name, Card);
            if (_chosen is { } volume) await SetMixerAsync(volume, ct); // plugged in again: back to the elves' volume
        }
        await ReadMixerAsync(ct);
    }

    /// <summary>The dashboard's slider: sets the card's volume now, and again whenever it's plugged back in.</summary>
    public async Task<bool> SetVolumeAsync(int percent, CancellationToken ct)
    {
        percent = Math.Clamp(percent, 0, 100);
        _chosen = percent;
        SaveVolume(percent);
        if (Card is null || FindCard(Card) is null) return false;
        if (!await SetMixerAsync(percent, ct)) return false;
        log.LogInformation("Santa's volume set from the dashboard: {Percent}%", percent);
        await ReadMixerAsync(ct);
        return true;
    }

    /// <summary>
    /// Plays the files one after another. Returns where, or null when it couldn't, with <see cref="SpeakerState.Problem"/>
    /// saying why.
    /// </summary>
    public async Task<string?> PlayAsync(IReadOnlyList<string> files, string what, CancellationToken ct)
    {
        if (Card is not null && FindCard(Card) is null)
            return Fail($"No sound card \"{Card}\": is the speakers' USB sound card plugged into the Pi?");
        var parts = _o.PlayCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return Fail("Voice:PlayCommand is empty");
        var args = parts.Skip(1).ToList();
        // aplay on its own goes to the Pi's default output (HDMI): send it to the speakers' card instead.
        if (Card is not null && Path.GetFileName(parts[0]) == "aplay" && !args.Contains("-D"))
            args.AddRange(["-D", $"plughw:CARD={Card},DEV=0"]);

        lock (_lock) _speaking = what;
        try
        {
            foreach (var file in files)
            {
                var (code, output) = await RunAsync(parts[0], [.. args, file], ct);
                if (code != 0) return Fail($"{parts[0]} said: {FirstLine(output) ?? $"exit code {code}"}");
            }
            lock (_lock) (_problem, _lastSpoke) = (null, DateTimeOffset.Now);
            return Where;
        }
        finally
        {
            lock (_lock) _speaking = null;
        }
    }

    string? Fail(string why)
    {
        lock (_lock) _problem = why;
        log.LogWarning("Santa couldn't speak at the station: {Why}", why);
        return null;
    }

    async Task<bool> SetMixerAsync(int percent, CancellationToken ct)
    {
        var (code, output) = await RunAsync("amixer", ["-q", "-M", "-c", Card!, "sset", _o.VolumeControl, $"{percent}%", "unmute"], ct);
        if (code != 0)
            log.LogWarning("Couldn't set the volume on sound card {Card} ({Control}): {Output}", Card, _o.VolumeControl, FirstLine(output));
        return code == 0;
    }

    async Task ReadMixerAsync(CancellationToken ct)
    {
        var (code, output) = await RunAsync("amixer", ["-M", "-c", Card!, "sget", _o.VolumeControl], ct);
        var volume = code == 0 && Percent().Match(output) is { Success: true } m ? int.Parse(m.Groups[1].Value) : (int?)null;
        lock (_lock) (_volume, _muted) = (volume, code == 0 && output.Contains("[off]"));
    }

    /// <summary>The card's own name if it's plugged in, from /proc/asound/cards; null if it isn't (or this isn't Linux).</summary>
    static string? FindCard(string id)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(Cards);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        //  2 [Audio          ]: USB-Audio - USB Audio
        //                       Generic USB Audio at usb-xhci-hcd.1-2, full speed
        for (var i = 0; i < lines.Length; i++)
        {
            if (CardLine().Match(lines[i]) is not { Success: true } m || m.Groups[1].Value != id) continue;
            var name = i + 1 < lines.Length ? lines[i + 1].Trim() : "";
            var at = name.IndexOf(" at ", StringComparison.Ordinal);
            return at > 0 ? name[..at] : name.Length > 0 ? name : m.Groups[2].Value.Trim();
        }
        return null;
    }

    static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    int? LoadVolume()
    {
        try
        {
            if (File.Exists(_file)) return JsonSerializer.Deserialize<Saved>(File.ReadAllText(_file))?.Volume;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            log.LogWarning("Couldn't read Santa's volume from {File}: {Message}", _file, ex.Message);
        }
        return null;
    }

    void SaveVolume(int percent)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, JsonSerializer.Serialize(new Saved(percent)));
    }

    sealed record Saved(int? Volume);

    [GeneratedRegex(@"^\s*\d+\s+\[(\S+?)\s*\]:\s*(.*)$")]
    private static partial Regex CardLine();

    [GeneratedRegex(@"\[(\d+)%\]")]
    private static partial Regex Percent();
}
