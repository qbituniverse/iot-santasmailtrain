using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace NorthPole.Hq.Santa;

/// <summary>
/// Gives Santa a voice with Piper (fast, offline neural TTS that runs happily on a Pi 5).
/// Piper runs as its own little HTTP server so the voice model stays loaded:
///   python3 -m piper.http_server -m en_GB-northern_english_male-medium
/// The WAV is saved next to the wish, played on the station speaker, and offered to the child's phone.
/// The station speaker is a Chromecast speaker such as a soundbar (<see cref="VoiceOptions.CastTo"/>), or the Pi's own
/// output through <see cref="VoiceOptions.PlayCommand"/>.
/// </summary>
public sealed class SantaVoice(IOptions<HqOptions> options, HttpClient http, CastSpeaker cast, ILogger<SantaVoice> log)
{
    const string TestLine = "Ho ho ho! This is Santa, checking the speaker at the North Pole. Can you hear me?";

    // One Santa at a time. Static, because each request gets its own SantaVoice (it's a typed HTTP client).
    static readonly SemaphoreSlim Speaker = new(1, 1);

    readonly HqOptions _o = options.Value;

    string VoiceDir => Directory.CreateDirectory(Path.Combine(Path.GetFullPath(_o.DataDirectory), "voice")).FullName;

    public Task<string?> SynthesizeAsync(Guid wishId, string text, CancellationToken ct) =>
        SynthesizeAsync($"{wishId}.wav", text, ct);

    async Task<string?> SynthesizeAsync(string fileName, string text, CancellationToken ct)
    {
        if (_o.Voice.Mode == VoiceMode.None || string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var response = await http.PostAsJsonAsync(
                $"{_o.Voice.PiperUrl.TrimEnd('/')}/synthesize",
                new PiperRequest(text, _o.Voice.PiperVoice, _o.Voice.LengthScale),
                ct);
            response.EnsureSuccessStatusCode();

            var file = Path.Combine(VoiceDir, fileName);
            await using (var stream = File.Create(file))
                await response.Content.CopyToAsync(stream, ct);
            return Path.GetFileName(file);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("Santa lost his voice (is Piper running at {Url}?): {Message}", _o.Voice.PiperUrl, ex.Message);
            return null;
        }
    }

    public string? PathOf(string? voiceFile) =>
        voiceFile is null ? null : Path.Combine(VoiceDir, Path.GetFileName(voiceFile));

    /// <summary>
    /// Plays the arrival jingle (if any) and then Santa, on the North Pole station speaker. Returns where it played,
    /// or null when station playback is off or there was nothing to play.
    /// </summary>
    public async Task<string?> PlayOnStationAsync(string? voiceFile, CancellationToken ct)
    {
        if (!_o.Voice.PlayOnStation) return null;

        var files = new List<string>();
        if (_o.Voice.ArrivalSound is { } jingle)
        {
            var jinglePath = Path.Combine(Path.GetFullPath(_o.DataDirectory), jingle);
            if (File.Exists(jinglePath)) files.Add(jinglePath);
        }
        if (PathOf(voiceFile) is { } path && File.Exists(path)) files.Add(path);
        if (files.Count == 0) return null;

        await Speaker.WaitAsync(ct);
        try
        {
            if (cast.Enabled)
            {
                if (await cast.PlayAsync(files, ct)) return _o.Voice.CastTo;
                log.LogWarning("Santa plays on the Pi's own output instead");
            }
            foreach (var file in files) await PlayAsync(file, ct);
            return "the Pi's own output";
        }
        finally
        {
            Speaker.Release();
        }
    }

    /// <summary>The longest line an elf can give Santa to test with, so a slip can't keep Piper busy for minutes.</summary>
    public const int LongestTestLine = 300;

    /// <summary>
    /// The dashboard's speaker check: Santa says <paramref name="text"/>, or his usual test line when it's empty, on the
    /// station speaker. Each test has a file of its own, gone once it has played.
    /// </summary>
    public async Task<string?> TestStationAsync(string? text, CancellationToken ct)
    {
        var line = string.IsNullOrWhiteSpace(text) ? TestLine : text.Trim();
        if (line.Length > LongestTestLine) line = line[..LongestTestLine];
        var file = await SynthesizeAsync($"speaker-test-{Guid.NewGuid():N}.wav", line, ct);
        if (file is null) return null;
        try
        {
            return await PlayOnStationAsync(file, ct);
        }
        finally
        {
            try { File.Delete(PathOf(file)!); }
            catch (IOException) { } // still open somewhere: a leftover test file does no harm
        }
    }

    async Task PlayAsync(string file, CancellationToken ct)
    {
        var parts = _o.Voice.PlayCommand.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        var psi = new ProcessStartInfo(parts[0]) { UseShellExecute = false };
        if (parts.Length > 1)
            foreach (var arg in parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(file);

        try
        {
            using var process = Process.Start(psi);
            if (process is not null) await process.WaitForExitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("Could not play {File} with '{Command}': {Message}", file, _o.Voice.PlayCommand, ex.Message);
        }
    }

    sealed record PiperRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("text")] string Text,
        [property: System.Text.Json.Serialization.JsonPropertyName("voice"),
                   System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? Voice,
        [property: System.Text.Json.Serialization.JsonPropertyName("length_scale")] double LengthScale);
}
