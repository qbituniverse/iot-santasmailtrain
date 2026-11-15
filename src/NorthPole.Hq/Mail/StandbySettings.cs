using System.Text.Json;
using Microsoft.Extensions.Options;
using NorthPole.Contracts;
using NorthPole.Messaging;

namespace NorthPole.Hq.Mail;

/// <summary>
/// How long the train and Santa's speaker wait with nothing happening before they sleep, as set by the dashboard's
/// sliders and kept in the data directory. The train's time goes to the signal box as a retained message, so it has it
/// from every start; the speaker's is used here, by <see cref="Santa.SpeakerLink"/>. 0 never sleeps.
/// </summary>
public sealed class StandbySettings(IOptions<HqOptions> options, MqttLink mqtt, ILogger<StandbySettings> log) : BackgroundService
{
    public const int MostMinutes = 30;
    const int DefaultMinutes = 5;

    readonly string _file = Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "standby.json");
    readonly Lock _lock = new();
    Saved? _saved;

    Saved Current
    {
        get
        {
            lock (_lock) return _saved ??= Load();
        }
    }

    public int TrainIdleMinutes => Current.TrainIdleMinutes;
    public int SpeakerIdleMinutes => Current.SpeakerIdleMinutes;

    public Task SetTrainAsync(int minutes)
    {
        Save(Current with { TrainIdleMinutes = Math.Clamp(minutes, 0, MostMinutes) });
        log.LogInformation("Train standby set from the dashboard: {Minutes} min (0 never)", TrainIdleMinutes);
        return PublishTrainAsync();
    }

    public void SetSpeaker(int minutes)
    {
        Save(Current with { SpeakerIdleMinutes = Math.Clamp(minutes, 0, MostMinutes) });
        log.LogInformation("Speaker standby set from the dashboard: {Minutes} min (0 never)", SpeakerIdleMinutes);
    }

    /// <summary>Hands the signal box the train's standby time whenever HQ connects to the broker.</summary>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        mqtt.ConnectionChanged += connected => { if (connected) _ = PublishTrainAsync(); };
        return mqtt.IsConnected ? PublishTrainAsync() : Task.CompletedTask;
    }

    Task PublishTrainAsync() =>
        mqtt.PublishAsync(Topics.TrainSettings, new TrainSettings(TrainIdleMinutes), retain: true);

    Saved Load()
    {
        try
        {
            if (File.Exists(_file) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(_file)) is { } saved) return saved;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            log.LogWarning("Couldn't read the standby times from {File} ({Message}); using {Minutes} min", _file, ex.Message, DefaultMinutes);
        }
        return new Saved(DefaultMinutes, DefaultMinutes);
    }

    void Save(Saved saved)
    {
        lock (_lock)
        {
            _saved = saved;
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(saved));
        }
    }

    sealed record Saved(int TrainIdleMinutes, int SpeakerIdleMinutes);
}
