namespace NorthPole.SignalBox;

public sealed class SignalBoxOptions
{
    public MqttOptions Mqtt { get; set; } = new();
    public TrainOptions Train { get; set; } = new();
    public TracksideOptions Trackside { get; set; } = new();
    public RunOptions Run { get; set; } = new();
}

public sealed class MqttOptions
{
    /// <summary>HQ runs an embedded broker: "localhost" when both apps share one Pi, HQ's name (e.g. "np.local") when split.</summary>
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string ClientId { get; set; } = "signalbox";
}

/// <summary>BlueZ: the Pi's own Bluetooth (or any standard USB adapter). PoweredUp: SharpBrick through a BLED112 dongle.</summary>
public enum TrainMode { Simulated, PoweredUp, BlueZ }

public sealed class TrainOptions
{
    public TrainMode Mode { get; set; } = TrainMode.Simulated;

    /// <summary>BlueZ mode: the Bluetooth adapter to use. hci0 is the Pi's built-in radio.</summary>
    public string BluetoothAdapter { get; set; } = "hci0";

    /// <summary>PoweredUp mode: serial device of the BlueGiga BLED112 dongle. Usually /dev/ttyACM0 on a Pi.</summary>
    public string BlueGigaPort { get; set; } = "/dev/ttyACM0";

    /// <summary>Hub port of the train motor (A = 0) and of the headlight LED (B = 1) in set 60337.</summary>
    public byte MotorPort { get; set; } = 0;
    public byte LightPort { get; set; } = 1;

    /// <summary>Flip if the locomotive drives backwards.</summary>
    public bool ReverseDirection { get; set; }

    public int CruiseSpeed { get; set; } = 45;
    public int ApproachSpeed { get; set; } = 25;

    /// <summary>Hard cap enforced by the safety interlock, whatever anyone asks for.</summary>
    public int MaxSpeed { get; set; } = 70;

    /// <summary>Refuse new mail runs below this battery level.</summary>
    public int LowBatteryPercent { get; set; } = 15;
}

public enum TracksideMode { Simulated, Gpio }

public sealed class TracksideOptions
{
    public TracksideMode Mode { get; set; } = TracksideMode.Simulated;

    /// <summary>The North Pole station's sensor: a magnetic door contact, with a magnet on the locomotive.</summary>
    public StationSensorOptions NorthPoleSensor { get; set; } = new() { Kind = SensorKind.Magnet, Pin = 17 };

    /// <summary>The mailbox (home) station's sensor: an HC-SR04. Kind None disables the return leg.</summary>
    public StationSensorOptions HomeSensor { get; set; } = new() { Kind = SensorKind.Ultrasonic, Pin = 27, TriggerPin = 22 };

    public int RedLedPin { get; set; } = 23;
    public int GreenLedPin { get; set; } = 24;

    /// <summary>
    /// Hardware PWM on GPIO18 for the SG90 mailbox-flag servo. Needs dtoverlay=pwm,pin=18,func=2 in config.txt.
    /// Chip and channel -1 find it by board: pwmchip0 channel 0 on a Pi 4, the RP1 block's channel 2 on a Pi 5.
    /// </summary>
    public bool ServoEnabled { get; set; } = true;
    public int ServoPwmChip { get; set; } = -1;
    public int ServoPwmChannel { get; set; } = -1;

    /// <summary>50 Hz servo: 5% duty ≈ 1 ms ≈ 0°, 7.5% ≈ 1.5 ms ≈ 90°. Tune to your flag.</summary>
    public double FlagDownDuty { get; set; } = 0.05;
    public double FlagUpDuty { get; set; } = 0.075;

    /// <summary>The 16x2 LCD at the mailbox. In simulated mode its screens go to the log.</summary>
    public StationBoardOptions Board { get; set; } = new();

    /// <summary>Simulation only: how long the pretend train takes between stations.</summary>
    public double SimulatedTravelSeconds { get; set; } = 6;
}

/// <summary>
/// An HD44780 character LCD (the JHD162A) in 4-bit mode: BCM pins for RS, E and D4 to D7. RW goes to ground.
/// The defaults use the bottom of the header, pins 29 to 40, clear of everything else.
/// </summary>
public sealed class StationBoardOptions
{
    public bool Enabled { get; set; } = true;
    public int RsPin { get; set; } = 5;
    public int EnablePin { get; set; } = 6;
    public int D4Pin { get; set; } = 13;
    public int D5Pin { get; set; } = 19;
    public int D6Pin { get; set; } = 26;
    public int D7Pin { get; set; } = 21;
}

/// <summary>How a station notices the train.</summary>
public enum SensorKind
{
    /// <summary>No sensor. Only for the mailbox: the train then parks at the North Pole.</summary>
    None,

    /// <summary>IR obstacle module (FC-51, TCRT5000): sees the coaches for as long as they pass.</summary>
    Infrared,

    /// <summary>Reed or Hall switch, with a magnet on the locomotive: a short pulse as the magnet passes.</summary>
    Magnet,

    /// <summary>HC-SR04 aimed across the track: pings and times the echo.</summary>
    Ultrasonic,
}

public sealed class StationSensorOptions
{
    public SensorKind Kind { get; set; } = SensorKind.Infrared;

    /// <summary>BCM pin of the sensor's output. For an HC-SR04, its Echo pin, through a 1 kΩ / 2 kΩ divider (Echo is 5 V).</summary>
    public int Pin { get; set; } = -1;

    /// <summary>Infrared and magnet: most IR modules and reed switches pull the pin low when they trigger.</summary>
    public bool ActiveLow { get; set; } = true;

    /// <summary>HC-SR04 only: BCM pin of its Trig input.</summary>
    public int TriggerPin { get; set; } = -1;

    /// <summary>HC-SR04 only: an echo from closer than this is the train. Aim it across the track at open space.</summary>
    public double DetectWithinCm { get; set; } = 8;
}

public sealed class RunOptions
{
    public double LoadingSeconds { get; set; } = 3;
    public double ArrivalTimeoutSeconds { get; set; } = 60;

    /// <summary>If HQ never says "Santa is done", go home anyway after this long.</summary>
    public double MaxWaitAtNorthPoleSeconds { get; set; } = 90;

    public double StatePublishSeconds { get; set; } = 10;
}
