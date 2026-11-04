using System.Text.Json;
using System.Text.Json.Serialization;

namespace NorthPole.Contracts;

/// <summary>MQTT topics. HQ = Raspberry Pi 5, SignalBox = Raspberry Pi 4 next to the track.</summary>
public static class Topics
{
    /// <summary>HQ → SignalBox: carry this wish from the mailbox to the North Pole.</summary>
    public const string Dispatch = "northpole/mailtrain/dispatch";

    /// <summary>SignalBox → HQ: progress of a mail run (Loading, Departed, Arrived...).</summary>
    public const string Progress = "northpole/mailtrain/progress";

    /// <summary>HQ → SignalBox: Santa has finished reading, the train may go home.</summary>
    public const string ReturnHome = "northpole/mailtrain/return";

    /// <summary>SignalBox → anyone (retained): live train state, battery, speed.</summary>
    public const string TrainState = "northpole/train/state";

    /// <summary>HQ → SignalBox: manual commands from the parents' dashboard.</summary>
    public const string Command = "northpole/train/command";
}

public enum MailStage
{
    /// <summary>Signal box accepted the job (it may still be queued behind another run).</summary>
    Accepted,
    /// <summary>Mailbox flag up, headlights blinking: the letter is being loaded.</summary>
    Loading,
    Departed,
    Arrived,
    Returning,
    Home,
    /// <summary>The run went wrong (timeout, hub lost). The train has been stopped.</summary>
    Failed,
    /// <summary>The signal box refused the job (low battery, hub not connected).</summary>
    Rejected,
}

/// <param name="WaitForSanta">False for test runs: go straight home instead of waiting for Santa to finish speaking.</param>
public sealed record MailDispatch(Guid WishId, string ChildName, DateTimeOffset At, bool WaitForSanta = true);

public sealed record MailProgress(Guid WishId, MailStage Stage, string? Detail, DateTimeOffset At);

public sealed record ReturnHome(Guid WishId);

public sealed record TrainState(
    bool HubConnected,
    int? BatteryPercent,
    int Speed,
    bool Busy,
    string TrainMode,
    string TracksideMode,
    DateTimeOffset At);

public enum TrainCommandKind { Stop, TestRun, LightsOn, LightsOff, Whistle }

public sealed record TrainCommand(TrainCommandKind Kind);

public static class NorthPoleJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);
}
