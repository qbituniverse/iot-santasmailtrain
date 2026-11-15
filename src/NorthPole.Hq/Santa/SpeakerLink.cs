using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace NorthPole.Hq.Santa;

/// <summary>What the dashboard shows about Santa's speaker.</summary>
/// <param name="Wanted">False after an elf pressed Disconnect: Santa plays on the Pi's own output until Connect.</param>
/// <param name="Playing">What the speaker is casting, by name, if anything (Santa shows as the Default Media Receiver).</param>
/// <param name="At">When the speaker last reported, so the dashboard can tell a fresh volume from an old one.</param>
public sealed record SpeakerState(bool Configured, string? Name, bool Wanted, bool Connected, string? Address,
    DateTimeOffset? ConnectedSince, double? Volume, bool? Muted, string? Playing, DateTimeOffset? At);

/// <summary>
/// HQ's standing link to the Chromecast speaker named in Voice:CastTo, for the dashboard: whether it answers and since
/// when, its volume and what it's playing. It connects from start-up and keeps trying while the speaker is away.
/// Disconnect closes the link and keeps Santa off the speaker until Connect. Santa's own casts open connections of
/// their own (<see cref="CastSpeaker"/>), and find the speaker's address here.
/// </summary>
public sealed class SpeakerLink(IOptions<HqOptions> options, ILogger<SpeakerLink> log) : BackgroundService
{
    static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);    // ask for its status this often
    static readonly TimeSpan Silence = TimeSpan.FromSeconds(30); // it pings every 5 s: this long without a word, it's gone
    static readonly TimeSpan Retry = TimeSpan.FromSeconds(15);   // look for it again after this

    readonly VoiceOptions _o = options.Value.Voice;
    readonly SemaphoreSlim _wake = new(0, 1);
    readonly Lock _lock = new();
    CancellationTokenSource? _link;
    volatile bool _wanted = true;
    bool _complained; // said once that it isn't answering: don't repeat it every 15 s
    long _heard;      // Environment.TickCount64 of its last message

    IPAddress? _address;
    bool _connected;
    DateTimeOffset? _since, _at;
    double? _volume;
    bool? _muted;
    string? _playing;

    public bool Configured => !string.IsNullOrWhiteSpace(_o.CastTo);
    public bool Wanted => _wanted;

    public SpeakerState State
    {
        get
        {
            lock (_lock)
                return new(Configured, _o.CastTo, _wanted, _connected, _address?.ToString(), _connected ? _since : null,
                    _volume, _muted, _playing, _at);
        }
    }

    public void Connect()
    {
        log.LogInformation("Connect to Santa's speaker from the dashboard");
        _wanted = true;
        try { _wake.Release(); }
        catch (SemaphoreFullException) { } // already woken
    }

    public void Disconnect()
    {
        log.LogInformation("Disconnect from Santa's speaker from the dashboard: Santa plays on the Pi until Connect");
        _wanted = false;
        try { _link?.Cancel(); }
        catch (ObjectDisposedException) { } // the link had just ended
    }

    /// <summary>The speaker's address: known already, or looked for now. Null if it doesn't answer.</summary>
    public async Task<IPAddress?> ResolveAsync(CancellationToken ct)
    {
        lock (_lock)
            if (_address is not null) return _address;
        var found = await FindAsync(ct);
        lock (_lock)
            return _address ??= found;
    }

    /// <summary>The address stopped working: look for the speaker by name next time.</summary>
    public void Forget()
    {
        if (IPAddress.TryParse(_o.CastTo, out _)) return; // given as an address: nothing to look for
        lock (_lock) _address = null;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (!Configured) return;
        await Task.Yield(); // not on the host's start-up path
        while (!stop.IsCancellationRequested)
        {
            if (_wanted)
            {
                try
                {
                    await HoldAsync(stop);
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    // Disconnect.
                }
                catch (Exception ex) when (!stop.IsCancellationRequested)
                {
                    bool wasConnected;
                    lock (_lock) wasConnected = _connected;
                    if (wasConnected || !_complained)
                        log.LogWarning("Santa's speaker '{Name}' isn't answering ({Message}); still trying", _o.CastTo, ex.Message);
                    _complained = true;
                    Forget();
                }
                lock (_lock) _connected = false;
            }
            try { await _wake.WaitAsync(Retry, stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Connects, then keeps the link until the speaker goes quiet or an elf presses Disconnect.</summary>
    async Task HoldAsync(CancellationToken stop)
    {
        using var link = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _link = link;
        try
        {
            var address = await ResolveAsync(link.Token) ?? throw new InvalidOperationException("not found on the network");
            await using var connection = await CastConnection.OpenAsync(address, link.Token);
            var cast = connection.Channel;
            await cast.SendAsync("receiver-0", CastChannel.Connection, new JsonObject { ["type"] = "CONNECT" }, link.Token);
            Volatile.Write(ref _heard, Environment.TickCount64);

            // One reader takes everything the speaker says: pings are answered, statuses noted.
            var reader = Task.Run(async () =>
            {
                while (true)
                {
                    var message = await cast.NextAsync(link.Token);
                    Volatile.Write(ref _heard, Environment.TickCount64);
                    if (message["type"]?.GetValue<string>() == "RECEIVER_STATUS") Update(message["status"]);
                }
            }, link.Token);

            lock (_lock) (_connected, _since) = (true, DateTimeOffset.Now);
            _complained = false;
            log.LogInformation("Connected to Santa's speaker '{Name}' at {Address}", _o.CastTo, address);
            try
            {
                while (true)
                {
                    await cast.SendAsync("receiver-0", CastChannel.Receiver, new JsonObject { ["type"] = "GET_STATUS" }, link.Token);
                    if (await Task.WhenAny(reader, Task.Delay(Poll, link.Token)) == reader) await reader; // its failure ends the link
                    link.Token.ThrowIfCancellationRequested();
                    if (Environment.TickCount64 - Volatile.Read(ref _heard) > Silence.TotalMilliseconds)
                        throw new TimeoutException("it stopped answering");
                }
            }
            finally
            {
                await link.CancelAsync(); // stops the reader
                try { await reader; }
                catch (Exception) { /* it ended with the link */ }
                log.LogInformation("Link to Santa's speaker '{Name}' closed", _o.CastTo);
            }
        }
        finally
        {
            _link = null;
        }
    }

    void Update(JsonNode? status)
    {
        if (status is null) return;
        lock (_lock)
        {
            if (status["volume"]?["level"] is { } level) _volume = level.GetValue<double>();
            if (status["volume"]?["muted"] is { } muted) _muted = muted.GetValue<bool>();
            _playing = status["applications"] is JsonArray apps
                ? apps.Where(a => a?["isIdleScreen"]?.GetValue<bool>() != true)
                    .Select(a => a?["displayName"]?.GetValue<string>())
                    .FirstOrDefault(name => !string.IsNullOrEmpty(name))
                : null;
            _at = DateTimeOffset.Now;
        }
    }

    /// <summary>The speaker named in CastTo: an IP address as it is, or a name looked for on the network.</summary>
    async Task<IPAddress?> FindAsync(CancellationToken ct)
    {
        if (IPAddress.TryParse(_o.CastTo, out var given)) return given;
        var found = await CastFinder.FindAsync(TimeSpan.FromSeconds(3), ct);
        var match = found.FirstOrDefault(d => string.Equals(d.Name, _o.CastTo, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            if (!_complained)
                log.LogWarning("No Chromecast speaker called '{Name}'. Speakers on the network: {Speakers}", _o.CastTo,
                    found.Count == 0 ? "none" : string.Join(", ", found.Select(d => $"'{d.Name}' ({d.Address})")));
            return null;
        }
        log.LogInformation("Found '{Name}' at {Address}", match.Name, match.Address);
        return match.Address;
    }
}
