using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace NorthPole.Hq.Santa;

/// <summary>
/// Gives Santa a voice with Piper (fast, offline neural TTS that runs happily on a Pi 5).
/// Piper runs as its own little HTTP server so the voice model stays loaded:
///   python3 -m piper.http_server -m en_GB-northern_english_male-medium
/// The WAV is saved next to the wish, played on the station speaker, and offered to the child's phone.
/// </summary>
public sealed class SantaVoice(IOptions<HqOptions> options, HttpClient http, ILogger<SantaVoice> log)
{
    readonly HqOptions _o = options.Value;
    readonly SemaphoreSlim _speaker = new(1, 1); // one Santa at a time

    string VoiceDir => Directory.CreateDirectory(Path.Combine(Path.GetFullPath(_o.DataDirectory), "voice")).FullName;

    public async Task<string?> SynthesizeAsync(Guid wishId, string text, CancellationToken ct)
    {
        if (_o.Voice.Mode == VoiceMode.None || string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var response = await http.PostAsJsonAsync(
                $"{_o.Voice.PiperUrl.TrimEnd('/')}/synthesize",
                new PiperRequest(text, _o.Voice.PiperVoice, _o.Voice.LengthScale),
                ct);
            response.EnsureSuccessStatusCode();

            var file = Path.Combine(VoiceDir, $"{wishId}.wav");
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

    /// <summary>Plays the arrival jingle (if any) and then Santa, on the North Pole station speaker.</summary>
    public async Task PlayOnStationAsync(string? voiceFile, CancellationToken ct)
    {
        if (!_o.Voice.PlayOnStation) return;

        await _speaker.WaitAsync(ct);
        try
        {
            if (_o.Voice.ArrivalSound is { } jingle)
            {
                var jinglePath = Path.Combine(Path.GetFullPath(_o.DataDirectory), jingle);
                if (File.Exists(jinglePath)) await PlayAsync(jinglePath, ct);
            }
            if (PathOf(voiceFile) is { } path && File.Exists(path)) await PlayAsync(path, ct);
        }
        finally
        {
            _speaker.Release();
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
