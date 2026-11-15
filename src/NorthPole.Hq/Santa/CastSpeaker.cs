using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace NorthPole.Hq.Santa;

/// <summary>
/// Plays Santa on a Chromecast speaker on the home network, such as a soundbar, instead of the Pi's own output.
/// HQ shares each file at an unguessable address for as long as it plays, and the speaker fetches it from here,
/// so nothing goes to the internet. Santa plays above the speaker's current volume (the TV's), within limits, and the
/// volume goes back afterwards. A cast that starts while the TV plays cuts in, and the TV comes back by itself.
/// </summary>
public sealed class CastSpeaker(IOptions<HqOptions> options, IServer server, ILogger<CastSpeaker> log)
{
    const int CastPort = 8009;
    const string MediaReceiver = "CC1AD845"; // Google's Default Media Receiver
    static readonly TimeSpan LongestFile = TimeSpan.FromMinutes(3);

    readonly VoiceOptions _o = options.Value.Voice;
    readonly ConcurrentDictionary<string, string> _shared = new();
    IPAddress? _address; // found once, looked for again if it stops answering

    public bool Enabled => !string.IsNullOrWhiteSpace(_o.CastTo);

    /// <summary>The file shared under <paramref name="token"/>, while it plays.</summary>
    public string? PathFor(string token) => _shared.TryGetValue(Path.GetFileNameWithoutExtension(token), out var path) ? path : null;

    /// <summary>Plays the files one after another. False if the speaker couldn't be reached or refused them.</summary>
    public async Task<bool> PlayAsync(IReadOnlyList<string> files, CancellationToken ct)
    {
        if (!Enabled || files.Count == 0) return false;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                _address ??= await FindAsync(ct);
                if (_address is null)
                {
                    log.LogWarning("No Chromecast speaker called '{Name}' answered on the network", _o.CastTo);
                    return false;
                }
                await PlayOnAsync(_address, files, ct);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                log.LogWarning("Santa couldn't play on '{Name}' ({Address}): {Message}", _o.CastTo, _address, ex.Message);
                _address = null; // its address may have changed: look for it again
            }
        }
        return false;
    }

    async Task PlayOnAsync(IPAddress address, IReadOnlyList<string> files, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(8));
            await tcp.ConnectAsync(address, CastPort, connect.Token);
        }
        // Cast devices present a certificate of their own making: on the home network there's nothing to check it against.
        await using var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = address.ToString() }, ct);
        var cast = new CastChannel(tls);
        var here = ((IPEndPoint)tcp.Client.LocalEndPoint!).Address; // the Pi's address on the speaker's network
        if (here.IsIPv4MappedToIPv6) here = here.MapToIPv4(); // the socket is dual-mode: give the speaker plain 192.168…

        await cast.SendAsync("receiver-0", CastChannel.Connection, new JsonObject { ["type"] = "CONNECT" }, ct);
        var status = await cast.RequestAsync("receiver-0", CastChannel.Receiver, new JsonObject { ["type"] = "GET_STATUS" }, ct);
        var before = status?["status"]?["volume"]?["level"]?.GetValue<double>();
        if (status?["status"]?["volume"]?["muted"]?.GetValue<bool>() == true)
            log.LogWarning("'{Name}' is muted, so Santa won't be heard", _o.CastTo); // someone muted it on purpose: leave it

        var volumeSet = false;
        string? session = null;
        try
        {
            if (before is { } level)
            {
                var santa = Math.Round(level >= _o.CastVolumeMax ? level : Math.Clamp(level + _o.CastVolumeAbove, _o.CastVolumeMin, _o.CastVolumeMax), 2);
                await cast.RequestAsync("receiver-0", CastChannel.Receiver,
                    new JsonObject { ["type"] = "SET_VOLUME", ["volume"] = new JsonObject { ["level"] = santa } }, ct);
                volumeSet = true;
                log.LogInformation("Santa on '{Name}' at volume {Santa:0.00} (it was {Before:0.00})", _o.CastTo, santa, level);
            }

            var launched = await cast.SendAndWaitAsync("receiver-0", CastChannel.Receiver,
                new JsonObject { ["type"] = "LAUNCH", ["appId"] = MediaReceiver },
                m => m["type"]?.GetValue<string>() == "RECEIVER_STATUS" && App(m) is { } a && a["transportId"] is not null,
                TimeSpan.FromSeconds(20), ct) ?? throw new TimeoutException("the speaker didn't start its media player");
            var app = App(launched)!;
            var player = app["transportId"]!.GetValue<string>();
            session = app["sessionId"]?.GetValue<string>();
            await cast.SendAsync(player, CastChannel.Connection, new JsonObject { ["type"] = "CONNECT" }, ct);

            foreach (var file in files) await PlayFileAsync(cast, player, here, file, ct);
        }
        finally
        {
            // Hand the speaker back as it was, even after a failure.
            using var tidy = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                if (session is not null)
                    await cast.RequestAsync("receiver-0", CastChannel.Receiver, new JsonObject { ["type"] = "STOP", ["sessionId"] = session }, tidy.Token);
                if (volumeSet)
                    await cast.RequestAsync("receiver-0", CastChannel.Receiver,
                        new JsonObject { ["type"] = "SET_VOLUME", ["volume"] = new JsonObject { ["level"] = before } }, tidy.Token);
            }
            catch (Exception ex)
            {
                log.LogWarning("Couldn't hand '{Name}' back as it was: {Message}", _o.CastTo, ex.Message);
            }
        }
    }

    async Task PlayFileAsync(CastChannel cast, string player, IPAddress here, string file, CancellationToken ct)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _shared[token] = file;
        try
        {
            var url = $"http://{(here.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{here}]" : here)}:{ServerPort()}/cast/{token}{Path.GetExtension(file)}";
            var load = new JsonObject
            {
                ["type"] = "LOAD",
                ["autoplay"] = true,
                ["media"] = new JsonObject
                {
                    ["contentId"] = url,
                    ["contentType"] = ContentType(file),
                    ["streamType"] = "BUFFERED",
                    ["metadata"] = new JsonObject { ["metadataType"] = 0, ["title"] = "Santa" },
                },
            };
            var started = false;
            var done = await cast.SendAndWaitAsync(player, CastChannel.Media, load, m =>
            {
                var type = m["type"]?.GetValue<string>();
                if (type is "LOAD_FAILED" or "LOAD_CANCELLED" or "INVALID_REQUEST") return true;
                if (type != "MEDIA_STATUS" || m["status"] is not JsonArray { Count: > 0 } list) return false;
                var state = list[0]?["playerState"]?.GetValue<string>();
                started |= state is "PLAYING" or "BUFFERING";
                return state == "IDLE" && list[0]?["idleReason"] is not null && (started || list[0]?["idleReason"]?.GetValue<string>() == "ERROR");
            }, LongestFile, ct) ?? throw new TimeoutException($"{Path.GetFileName(file)} didn't finish playing");

            var reason = done["status"] is JsonArray { Count: > 0 } s ? s[0]?["idleReason"]?.GetValue<string>() : done["type"]?.GetValue<string>();
            if (reason != "FINISHED") throw new InvalidOperationException($"the speaker stopped {Path.GetFileName(file)}: {reason}");
        }
        finally
        {
            _shared.TryRemove(token, out _);
        }
    }

    static JsonNode? App(JsonNode m) =>
        m["status"]?["applications"] is JsonArray apps ? apps.FirstOrDefault(a => a?["appId"]?.GetValue<string>() == MediaReceiver) : null;

    static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".ogg" => "audio/ogg",
        _ => "audio/wav",
    };

    /// <summary>The port HQ serves on, for the speaker to fetch from.</summary>
    int ServerPort()
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        foreach (var a in addresses)
        {
            var uri = new Uri(a.Replace("://+", "://0.0.0.0").Replace("://*", "://0.0.0.0"));
            if (uri.Scheme == "http") return uri.Port;
        }
        return 80;
    }

    /// <summary>The speaker named in CastTo: an IP address as it is, or a name looked for on the network.</summary>
    async Task<IPAddress?> FindAsync(CancellationToken ct)
    {
        if (IPAddress.TryParse(_o.CastTo, out var given)) return given;
        var found = await CastFinder.FindAsync(TimeSpan.FromSeconds(3), ct);
        var match = found.FirstOrDefault(d => string.Equals(d.Name, _o.CastTo, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            log.LogWarning("Chromecast speakers on the network: {Speakers}",
                found.Count == 0 ? "none" : string.Join(", ", found.Select(d => $"'{d.Name}' ({d.Address})")));
            return null;
        }
        log.LogInformation("Found '{Name}' at {Address}", match.Name, match.Address);
        return match.Address;
    }
}

/// <summary>
/// One Cast v2 connection: length-prefixed protobuf CastMessages over TLS, each carrying a JSON payload. The few protobuf
/// fields involved are written and read by hand. Heartbeat pings are answered as they come.
/// </summary>
sealed class CastChannel(Stream stream)
{
    public const string Connection = "urn:x-cast:com.google.cast.tp.connection";
    public const string Heartbeat = "urn:x-cast:com.google.cast.tp.heartbeat";
    public const string Receiver = "urn:x-cast:com.google.cast.receiver";
    public const string Media = "urn:x-cast:com.google.cast.media";

    int _requestId;

    public async Task<int> SendAsync(string destination, string ns, JsonObject payload, CancellationToken ct)
    {
        var id = 0;
        if (ns != Connection && ns != Heartbeat) payload["requestId"] = id = Interlocked.Increment(ref _requestId);
        using var body = new MemoryStream();
        Varint(body, 1 << 3); Varint(body, 0);                 // protocol_version CASTV2_1_0
        Text(body, 2, "sender-0");                              // source_id
        Text(body, 3, destination);                             // destination_id
        Text(body, 4, ns);                                      // namespace
        Varint(body, 5 << 3); Varint(body, 0);                  // payload_type STRING
        Text(body, 6, payload.ToJsonString());                  // payload_utf8
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, (int)body.Length);
        body.ToArray().CopyTo(frame, 4);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
        return id;
    }

    /// <summary>Sends, then waits for the reply that carries the same request id.</summary>
    public async Task<JsonNode?> RequestAsync(string destination, string ns, JsonObject payload, CancellationToken ct)
    {
        var id = await SendAsync(destination, ns, payload, ct);
        return await WaitAsync(m => m["requestId"]?.GetValue<int>() == id, TimeSpan.FromSeconds(8), ct);
    }

    public async Task<JsonNode?> SendAndWaitAsync(string destination, string ns, JsonObject payload, Func<JsonNode, bool> until,
        TimeSpan timeout, CancellationToken ct)
    {
        await SendAsync(destination, ns, payload, ct);
        return await WaitAsync(until, timeout, ct);
    }

    async Task<JsonNode?> WaitAsync(Func<JsonNode, bool> until, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            while (true)
            {
                var (source, ns, message) = await ReadAsync(limit.Token);
                if (ns == Heartbeat && message["type"]?.GetValue<string>() == "PING")
                {
                    await SendAsync(source, Heartbeat, new JsonObject { ["type"] = "PONG" }, ct);
                    continue;
                }
                if (until(message)) return message;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // timed out
        }
    }

    async Task<(string Source, string Namespace, JsonNode Message)> ReadAsync(CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var body = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
        await stream.ReadExactlyAsync(body, ct);

        string source = "", ns = "", payload = "{}";
        for (var i = 0; i < body.Length;)
        {
            var key = (int)ReadVarint(body, ref i);
            switch (key & 7)
            {
                case 0: ReadVarint(body, ref i); break;
                case 2:
                    var length = (int)ReadVarint(body, ref i);
                    var value = Encoding.UTF8.GetString(body, i, length);
                    i += length;
                    switch (key >> 3)
                    {
                        case 2: source = value; break;
                        case 4: ns = value; break;
                        case 6: payload = value; break;
                    }
                    break;
                default: throw new InvalidDataException($"unexpected protobuf wire type {key & 7}");
            }
        }
        return (source, ns, JsonNode.Parse(payload) ?? new JsonObject());
    }

    static void Text(Stream s, int field, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Varint(s, (ulong)(field << 3 | 2));
        Varint(s, (ulong)bytes.Length);
        s.Write(bytes);
    }

    static void Varint(Stream s, ulong value)
    {
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            s.WriteByte(value == 0 ? b : (byte)(b | 0x80));
        } while (value != 0);
    }

    static ulong ReadVarint(byte[] data, ref int i)
    {
        ulong value = 0;
        for (var shift = 0; ; shift += 7)
        {
            var b = data[i++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
    }
}

/// <summary>A Chromecast device as it announces itself: its friendly name (as the Google Home app shows it) and address.</summary>
public sealed record CastDevice(string Name, IPAddress Address);

/// <summary>
/// Finds Chromecast devices with one multicast DNS question (_googlecast._tcp.local). It asks from an ordinary port, so
/// the devices answer it directly and it runs beside the Pi's own mDNS service (avahi).
/// </summary>
public static class CastFinder
{
    public static async Task<IReadOnlyList<CastDevice>> FindAsync(TimeSpan listen, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var query = Query("_googlecast._tcp.local");
        await udp.SendAsync(query, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353), ct);

        var found = new Dictionary<string, CastDevice>(StringComparer.OrdinalIgnoreCase);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(listen);
        try
        {
            while (true)
            {
                var reply = await udp.ReceiveAsync(limit.Token);
                try
                {
                    foreach (var name in FriendlyNames(reply.Buffer))
                        found[name] = new CastDevice(name, reply.RemoteEndPoint.Address);
                }
                catch (IndexOutOfRangeException)
                {
                    // A packet that isn't what it claims: skip it.
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Listened long enough.
        }
        return [.. found.Values];
    }

    static byte[] Query(string name)
    {
        using var q = new MemoryStream();
        q.Write([0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0]); // id 0, flags 0, one question
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            q.WriteByte((byte)bytes.Length);
            q.Write(bytes);
        }
        q.Write([0, 0, 12, 0, 1]); // root, type PTR, class IN
        return q.ToArray();
    }

    /// <summary>The fn= values in the TXT records of a reply: each Chromecast's friendly name.</summary>
    static IEnumerable<string> FriendlyNames(byte[] d)
    {
        var names = new List<string>();
        int questions = d[4] << 8 | d[5], records = (d[6] << 8 | d[7]) + (d[8] << 8 | d[9]) + (d[10] << 8 | d[11]);
        var i = 12;
        for (var q = 0; q < questions; q++) { SkipName(d, ref i); i += 4; }
        for (var r = 0; r < records; r++)
        {
            SkipName(d, ref i);
            int type = d[i] << 8 | d[i + 1], length = d[i + 8] << 8 | d[i + 9];
            i += 10;
            if (type == 16) // TXT: length-prefixed strings
                for (var k = i; k < i + length;)
                {
                    var s = Encoding.UTF8.GetString(d, k + 1, d[k]);
                    if (s.StartsWith("fn=", StringComparison.Ordinal)) names.Add(s[3..]);
                    k += 1 + d[k];
                }
            i += length;
        }
        return names;
    }

    static void SkipName(byte[] d, ref int i)
    {
        while (true)
        {
            var length = d[i];
            if (length == 0) { i++; return; }
            if ((length & 0xC0) == 0xC0) { i += 2; return; } // a pointer ends the name
            i += 1 + length;
        }
    }
}
