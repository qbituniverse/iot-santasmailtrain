using System.Device.Gpio;
using System.Device.Pwm;
using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace NorthPole.SignalBox;

public enum Station { Home, NorthPole }

public enum SignalAspect { Red, Green, Off }

/// <summary>Everything bolted to the track: station sensors, the mailbox flag and the signal lights.</summary>
public interface ITrackside : IDisposable
{
    string Mode { get; }
    bool HasHomeSensor { get; }

    /// <summary>Completes when the train's nose reaches the station sensor.</summary>
    Task WaitForTrainAsync(Station station, CancellationToken ct);

    void SetFlag(bool up);
    void SetSignal(SignalAspect aspect);

    /// <summary>Simulation hook: tells the fake track the train is moving.</summary>
    void NotifyTrainMoving(Station towards) { }
}

/// <summary>
/// Real hardware on the Pi's GPIO header (BCM numbering, the same on a Pi 4 and a Pi 5):
///   North Pole magnetic contact → GPIO17, mailbox HC-SR04 Trig → GPIO22 and Echo → GPIO27 (through a divider),
///   red LED → GPIO23, green LED → GPIO24, SG90 servo signal → GPIO18 (hardware PWM).
/// On a Pi 5 the GPIO goes through libgpiod (the RP1 chip), so libgpiod must be installed.
/// </summary>
public sealed class GpioTrackside : ITrackside
{
    readonly TracksideOptions _o;
    readonly ILogger<GpioTrackside> _log;
    readonly GpioController _gpio = PiGpio.Open();
    readonly PwmChannel? _servo;
    readonly IStationSensor _northPole;
    readonly IStationSensor? _home;

    public GpioTrackside(IOptions<SignalBoxOptions> options, ILogger<GpioTrackside> log)
    {
        _o = options.Value.Trackside;
        _log = log;

        _northPole = StationSensor.Create(_gpio, _o.NorthPoleSensor)
                     ?? throw new InvalidOperationException("The North Pole station needs a sensor (Trackside:NorthPoleSensor)");
        _home = StationSensor.Create(_gpio, _o.HomeSensor);
        _gpio.OpenPin(_o.RedLedPin, PinMode.Output, PinValue.High);
        _gpio.OpenPin(_o.GreenLedPin, PinMode.Output, PinValue.Low);

        if (_o.ServoEnabled)
        {
            try
            {
                var (chip, channel) = ServoPwm.Locate(_o);
                _servo = PwmChannel.Create(chip, channel, 50, _o.FlagDownDuty);
                _servo.Start();
                _log.LogInformation("Mailbox flag servo on pwmchip{Chip} channel {Channel}", chip, channel);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Servo PWM unavailable. Add 'dtoverlay=pwm,pin=18,func=2' to /boot/firmware/config.txt and reboot");
            }
        }
    }

    public string Mode => "Gpio";
    public bool HasHomeSensor => _home is not null;

    public async Task WaitForTrainAsync(Station station, CancellationToken ct)
    {
        var sensor = station == Station.NorthPole ? _northPole
            : _home ?? throw new InvalidOperationException("The mailbox has no sensor");
        await sensor.WaitForTrainAsync(ct);
        _log.LogInformation("Train detected at {Station}", station);
    }

    public void SetFlag(bool up)
    {
        if (_servo is null) return;
        _servo.DutyCycle = up ? _o.FlagUpDuty : _o.FlagDownDuty;
    }

    public void SetSignal(SignalAspect aspect)
    {
        _gpio.Write(_o.RedLedPin, aspect == SignalAspect.Red ? PinValue.High : PinValue.Low);
        _gpio.Write(_o.GreenLedPin, aspect == SignalAspect.Green ? PinValue.High : PinValue.Low);
    }

    public void Dispose()
    {
        try { SetSignal(SignalAspect.Off); } catch { /* shutting down */ }
        _servo?.Stop();
        _servo?.Dispose();
        _gpio.Dispose();
    }
}

/// <summary>
/// Finds the hardware PWM channel behind GPIO18. The same overlay (dtoverlay=pwm,pin=18,func=2) works on both boards,
/// but the channel doesn't: on a Pi 4 GPIO18 is channel 0 of pwmchip0; on a Pi 5 it is channel 2 of the RP1 PWM block,
/// device 1f00098000.pwm, whose pwmchip number changed between kernels (pwmchip2 on 6.6, pwmchip0 on 6.12). So on a
/// Pi 5 the chip is found by its device link. Settings of 0 or more override either number.
/// </summary>
public static class ServoPwm
{
    const string Rp1PwmBlock = "1f00098000.pwm"; // RP1's PWM0; its sibling 1f0009c000.pwm drives the cooling fan

    /// <param name="root">The filesystem root, for tests.</param>
    /// <param name="deviceLink">Reads a pwmchip's "device" symlink target, for tests.</param>
    public static (int Chip, int Channel) Locate(TracksideOptions o, string root = "/", Func<string, string?>? deviceLink = null)
    {
        if (o.ServoPwmChip >= 0 && o.ServoPwmChannel >= 0) return (o.ServoPwmChip, o.ServoPwmChannel);
        deviceLink ??= chipDir => new FileInfo(Path.Combine(chipDir, "device")).LinkTarget;

        var model = Path.Combine(root, "proc", "device-tree", "model");
        if (!File.Exists(model) || !File.ReadAllText(model).Contains("Raspberry Pi 5"))
            return (Pick(o.ServoPwmChip, 0), Pick(o.ServoPwmChannel, 0));

        var pwm = Path.Combine(root, "sys", "class", "pwm");
        var chip = (Directory.Exists(pwm) ? Directory.GetDirectories(pwm, "pwmchip*") : [])
            .FirstOrDefault(dir => Path.GetFileName(deviceLink(dir)?.TrimEnd('/')) == Rp1PwmBlock)
            ?? throw new InvalidOperationException("No RP1 PWM block on this Pi 5: is dtoverlay=pwm,pin=18,func=2 in config.txt?");
        return (Pick(o.ServoPwmChip, int.Parse(Path.GetFileName(chip)["pwmchip".Length..])), Pick(o.ServoPwmChannel, 2));
    }

    static int Pick(int setting, int found) => setting >= 0 ? setting : found;
}

/// <summary>One station's way of noticing the train.</summary>
public interface IStationSensor
{
    /// <summary>Completes when the train reaches the sensor, straight away if it is already there.</summary>
    Task WaitForTrainAsync(CancellationToken ct);
}

public static class StationSensor
{
    /// <summary>The sensor a station's options describe, or null when it has none.</summary>
    public static IStationSensor? Create(GpioController gpio, StationSensorOptions o) =>
        o.Kind == SensorKind.None || o.Pin < 0 ? null
        : o.Kind == SensorKind.Ultrasonic ? new UltrasonicSensor(gpio, o)
        : new SwitchSensor(gpio, o);
}

/// <summary>
/// A sensor that switches a pin when it sees the train: an IR module, or a reed or Hall switch.
/// An IR module sees the coaches for as long as they pass, so a trigger must still hold after 30 ms, which ignores
/// flickers from sunlight or a passing hand. A magnet closes the switch only while it passes, perhaps for a few tens
/// of milliseconds at cruise speed, and nothing else can close it, so its first edge counts.
/// </summary>
public sealed class SwitchSensor : IStationSensor
{
    readonly GpioController _gpio;
    readonly StationSensorOptions _o;

    public SwitchSensor(GpioController gpio, StationSensorOptions o)
    {
        _gpio = gpio;
        _o = o;
        // Pull the pin to its idle level, so a switch that only ever connects one way still reads cleanly.
        _gpio.OpenPin(o.Pin, o.ActiveLow ? PinMode.InputPullUp : PinMode.InputPullDown);
    }

    public async Task WaitForTrainAsync(CancellationToken ct)
    {
        var edge = _o.ActiveLow ? PinEventTypes.Falling : PinEventTypes.Rising;
        var active = _o.ActiveLow ? PinValue.Low : PinValue.High;

        // The train might already be parked in front of the sensor.
        if (_gpio.Read(_o.Pin) == active) return;

        while (true)
        {
            await _gpio.WaitForEventAsync(_o.Pin, edge, ct);
            ct.ThrowIfCancellationRequested(); // the driver reports a cancelled wait as "timed out"
            if (_o.Kind == SensorKind.Magnet) return;
            await Task.Delay(30, ct); // debounce: ignore flickers from sunlight or a passing hand
            if (_gpio.Read(_o.Pin) == active) return;
        }
    }
}

/// <summary>
/// An HC-SR04 aimed across the track at open space. It pings about 15 times a second, and two echoes in a row from
/// closer than DetectWithinCm are the train (one alone could be a stray echo). It measures the echo the way the
/// .NET IoT Hcsr04 binding does, without pulling in the whole bindings package for one sensor.
/// </summary>
public sealed class UltrasonicSensor : IStationSensor
{
    static readonly TimeSpan TriggerPulse = TimeSpan.FromMicroseconds(10);
    static readonly TimeSpan PingGap = TimeSpan.FromMilliseconds(65); // the datasheet asks for 60 ms between pings
    static readonly TimeSpan EchoTimeout = TimeSpan.FromMilliseconds(30); // past 4 m and back; "nothing" is a ~38 ms pulse

    readonly GpioController _gpio;
    readonly StationSensorOptions _o;

    public UltrasonicSensor(GpioController gpio, StationSensorOptions o)
    {
        if (o.TriggerPin < 0) throw new InvalidOperationException("An HC-SR04 needs its Trig pin (TriggerPin)");
        _gpio = gpio;
        _o = o;
        _gpio.OpenPin(o.TriggerPin, PinMode.Output, PinValue.Low);
        _gpio.OpenPin(o.Pin, PinMode.Input);
    }

    public async Task WaitForTrainAsync(CancellationToken ct)
    {
        var close = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            close = MeasureCm() is { } cm && cm < _o.DetectWithinCm ? close + 1 : 0;
            if (close >= 2) return;
            await Task.Delay(PingGap, ct);
        }
    }

    /// <summary>One ping: a 10 µs pulse on Trig, then Echo stays high for the round trip (58 µs a centimetre).</summary>
    public double? MeasureCm()
    {
        var clock = Stopwatch.StartNew();
        _gpio.Write(_o.TriggerPin, PinValue.High);
        while (clock.Elapsed < TriggerPulse) { } // too short to sleep: spin
        _gpio.Write(_o.TriggerPin, PinValue.Low);

        clock.Restart();
        while (_gpio.Read(_o.Pin) == PinValue.Low)
            if (clock.Elapsed > EchoTimeout) return null;
        var start = clock.Elapsed;
        while (_gpio.Read(_o.Pin) == PinValue.High)
            if (clock.Elapsed > EchoTimeout) return null;
        return (clock.Elapsed - start).TotalMilliseconds / 2 * 34.3;
    }
}

/// <summary>Fake track for the laptop: the train "arrives" a few seconds after it starts moving.</summary>
public sealed class SimulatedTrackside(IOptions<SignalBoxOptions> options, ILogger<SimulatedTrackside> log) : ITrackside
{
    readonly TimeSpan _travel = TimeSpan.FromSeconds(options.Value.Trackside.SimulatedTravelSeconds);
    DateTimeOffset _departedAt = DateTimeOffset.UtcNow;

    public string Mode => "Simulated";
    public bool HasHomeSensor => true;

    public void NotifyTrainMoving(Station towards) => _departedAt = DateTimeOffset.UtcNow;

    public async Task WaitForTrainAsync(Station station, CancellationToken ct)
    {
        var remaining = _departedAt + _travel - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
        log.LogInformation("(sim) Train detected at {Station}", station);
    }

    public void SetFlag(bool up) => log.LogInformation("(sim) Mailbox flag {State}", up ? "UP" : "down");

    public void SetSignal(SignalAspect aspect) => log.LogInformation("(sim) Signal {Aspect}", aspect);

    public void Dispose() { }
}
