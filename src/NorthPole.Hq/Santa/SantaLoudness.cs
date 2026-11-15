using System.Text.Json;
using Microsoft.Extensions.Options;

namespace NorthPole.Hq.Santa;

/// <summary>
/// How loud Santa plays on a Chromecast speaker: this far above its current volume (the TV's), within the limits in
/// Voice:CastVolumeMin and CastVolumeMax. Voice:CastVolumeAbove is the start; the dashboard's slider changes it, and
/// the choice is kept in the data directory, so it outlasts restarts and updates.
/// </summary>
public sealed class SantaLoudness
{
    /// <summary>The slider goes from level with the TV to this far above it.</summary>
    public const double MostAbove = 0.3;

    readonly VoiceOptions _o;
    readonly string _file;
    readonly ILogger<SantaLoudness> _log;
    readonly Lock _lock = new();
    double _above;

    public SantaLoudness(IOptions<HqOptions> options, ILogger<SantaLoudness> log)
    {
        _o = options.Value.Voice;
        _log = log;
        _file = Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "santa-loudness.json");
        _above = Math.Clamp(_o.CastVolumeAbove, 0, MostAbove);
        try
        {
            if (File.Exists(_file) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(_file)) is { } saved)
                _above = Math.Clamp(saved.Above, 0, MostAbove);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            log.LogWarning("Couldn't read Santa's loudness from {File} ({Message}); using {Above:0.00}", _file, ex.Message, _above);
        }
    }

    public double Above
    {
        get { lock (_lock) return _above; }
    }

    public double Min => _o.CastVolumeMin;
    public double Max => _o.CastVolumeMax;

    /// <summary>The volume Santa plays at, when the speaker is at <paramref name="current"/>.</summary>
    public double LevelFor(double current) =>
        Math.Round(current >= Max ? current : Math.Clamp(current + Above, Min, Max), 2);

    public void Set(double above)
    {
        above = Math.Round(Math.Clamp(above, 0, MostAbove), 2);
        lock (_lock)
        {
            _above = above;
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(new Saved(above)));
        }
        _log.LogInformation("Santa's loudness set from the dashboard: the volume + {Above:0}", above * 100);
    }

    sealed record Saved(double Above);
}
