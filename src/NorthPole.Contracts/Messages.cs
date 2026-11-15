using System.Text.Json;
using System.Text.Json.Serialization;

namespace NorthPole.Contracts;

/// <summary>MQTT topics between HQ (the web app and Santa) and the SignalBox (the train and the track).</summary>
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

/// <param name="BatteryPercent">The hub's own battery reading, as LEGO works it out.</param>
/// <param name="MaxSpeed">The signal box's speed cap, so the dashboard's speed slider can stop there.</param>
/// <param name="BatteryVolts">The hub's measured battery voltage: six fresh AA or AAA cells give about 9 V.</param>
/// <param name="ConnectedSince">When the hub connected, so the dashboard can show how long it has been.</param>
/// <param name="LowBatteryPercent">Below this the signal box turns letters away, so the dashboard can say why.</param>
/// <param name="Listening">
/// The signal box is looking for the hub, so pressing its green button connects it. It does from start-up and after
/// a Connect, and stops after a Disconnect.
/// </param>
public sealed record TrainState(
    bool HubConnected,
    int? BatteryPercent,
    int Speed,
    bool Busy,
    string TrainMode,
    string TracksideMode,
    DateTimeOffset At,
    int MaxSpeed = 0,
    double? BatteryVolts = null,
    DateTimeOffset? ConnectedSince = null,
    int LowBatteryPercent = 0,
    bool Listening = false);

/// <summary>
/// Stop brakes and ends the mail run in progress. Drive is the train driver: it sets the motor to
/// <see cref="TrainCommand.Speed"/> (above 0 forward, below 0 in reverse, 0 brakes), and only between mail runs. Disconnect stops the train, lets the hub go
/// and stops looking for it; Connect looks for it again, so the hub's green button connects it.
/// </summary>
public enum TrainCommandKind { Stop, TestRun, LightsOn, LightsOff, Whistle, Drive, Connect, Disconnect }

public sealed record TrainCommand(TrainCommandKind Kind, int? Speed = null);

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
