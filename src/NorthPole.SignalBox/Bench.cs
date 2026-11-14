using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace NorthPole.SignalBox;

/// <summary>
/// The bench test mode: drives one trackside part at a time through the same classes the mail run uses, so a part that
/// passes here behaves the same on the day. Run on the Pi with the signal box's own binary, with the service stopped:
///   NorthPole.SignalBox bench signal                 red, green, four swaps, off
///   NorthPole.SignalBox bench signal red|green|off [seconds]
///   NorthPole.SignalBox bench wait [seconds]         count magnet passes at the North Pole, signal green on each
///   NorthPole.SignalBox bench distance [seconds]     live readings from the mailbox HC-SR04
///   NorthPole.SignalBox bench flag                   the mailbox flag: down, up, down, up, down
///   NorthPole.SignalBox bench flag up|down [seconds]
///   NorthPole.SignalBox bench flag duty 0.06 [seconds]   any position, to tune FlagUpDuty and FlagDownDuty
/// It always uses the real GPIO, whatever Trackside:Mode says, and opens only the pins its command needs.
/// </summary>
public static class Bench
{
    public static async Task<int> RunAsync(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var options = config.GetSection("SignalBox").Get<SignalBoxOptions>() ?? new();

        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        using var loggers = LoggerFactory.Create(b =>
        {
            b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
             .SetMinimumLevel(LogLevel.Information);
            // The trackside logs every detection; while a magnet is held that would repeat. The bench reports passes itself.
            if (command == "wait") b.AddFilter(typeof(GpioTrackside).FullName, LogLevel.Warning);
        });
        var log = loggers.CreateLogger("Bench");

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

        try
        {
            return command switch
            {
                "signal" => await SignalAsync(args[1..], options, loggers, log, stop.Token),
                "wait" => await WaitAsync(args[1..], options, loggers, log, stop.Token),
                "distance" => await DistanceAsync(args[1..], options, log, stop.Token),
                "flag" => await FlagAsync(args[1..], options, loggers, log, stop.Token),
                _ => Usage(log),
            };
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            log.LogInformation("Stopped");
            return 130;
        }
        catch (ArgumentException ex)
        {
            log.LogError("{Problem}", ex.Message);
            Usage(log);
            return 2;
        }
        catch (Exception ex)
        {
            log.LogError("The bench test failed: {Problem} ({Kind}). It needs the Pi's GPIO: run it on the Pi, with the signal box service stopped",
                ex.Message, ex.GetType().Name);
            return 1;
        }
    }

    static int Usage(ILogger log)
    {
        log.LogInformation("Usage: NorthPole.SignalBox bench signal [red|green|off] [seconds] | bench wait [seconds] | bench distance [seconds] | bench flag [up|down [seconds] | duty 0.06 [seconds]]");
        return 2;
    }

    /// <summary>The signal LEDs: a fixed show, or one aspect held for a while. Both end with the signal off.</summary>
    static async Task<int> SignalAsync(string[] args, SignalBoxOptions options, ILoggerFactory loggers, ILogger log, CancellationToken ct)
    {
        var o = options.Trackside;
        SignalAspect? hold = args.Length == 0 ? null : args[0].ToLowerInvariant() switch
        {
            "red" => SignalAspect.Red,
            "green" => SignalAspect.Green,
            "off" => SignalAspect.Off,
            _ => throw new ArgumentException($"Unknown signal aspect '{args[0]}': use red, green or off"),
        };
        var seconds = args.Length > 1 ? Seconds(args[1]) : 5;
        using var track = Open(options, loggers);
        if (hold is { } aspect)
        {
            log.LogInformation("Signal {Aspect} for {Seconds} s (red GPIO{Red}, green GPIO{Green})", aspect, seconds, o.RedLedPin, o.GreenLedPin);
            track.SetSignal(aspect);
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        }
        else
        {
            log.LogInformation("Signal test on red GPIO{Red} and green GPIO{Green}: red 3 s, green 3 s, four swaps, then off", o.RedLedPin, o.GreenLedPin);
            log.LogInformation("Signal red");
            track.SetSignal(SignalAspect.Red);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            log.LogInformation("Signal green");
            track.SetSignal(SignalAspect.Green);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            log.LogInformation("Four swaps");
            for (var i = 0; i < 4; i++)
            {
                track.SetSignal(SignalAspect.Red);
                await Task.Delay(TimeSpan.FromMilliseconds(600), ct);
                track.SetSignal(SignalAspect.Green);
                await Task.Delay(TimeSpan.FromMilliseconds(600), ct);
            }
        }
        track.SetSignal(SignalAspect.Off);
        log.LogInformation("Signal off");
        return 0;
    }

    /// <summary>
    /// Watches the North Pole sensor the way a mail run does, but over and over: each pass turns the signal green for
    /// two seconds, then back to red. A magnet held at the contact counts once.
    /// </summary>
    static async Task<int> WaitAsync(string[] args, SignalBoxOptions options, ILoggerFactory loggers, ILogger log, CancellationToken ct)
    {
        var seconds = args.Length > 0 ? Seconds(args[0]) : 30;
        var sensor = options.Trackside.NorthPoleSensor;
        using var track = Open(options, loggers);
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromSeconds(seconds));

        log.LogInformation("Watching the North Pole sensor ({Kind} on GPIO{Pin}) for {Seconds} s: bring the magnet to the contact",
            sensor.Kind, sensor.Pin, seconds);
        track.SetSignal(SignalAspect.Red);
        var clock = Stopwatch.StartNew();
        var passes = 0;
        try
        {
            while (true)
            {
                var asked = clock.Elapsed;
                await track.WaitForTrainAsync(Station.NorthPole, window.Token);
                if (passes > 0 && clock.Elapsed - asked < TimeSpan.FromMilliseconds(30))
                {
                    // It answered at once: the magnet from the last pass is still there.
                    await Task.Delay(TimeSpan.FromMilliseconds(100), window.Token);
                    continue;
                }
                passes++;
                log.LogInformation("Pass {Pass} at {At:0.0} s: signal green", passes, clock.Elapsed.TotalSeconds);
                track.SetSignal(SignalAspect.Green);
                await Task.Delay(TimeSpan.FromSeconds(2), window.Token);
                track.SetSignal(SignalAspect.Red);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The watch window ended.
        }
        track.SetSignal(SignalAspect.Off);
        if (passes == 0)
            log.LogWarning("No magnet seen in {Seconds} s. Bring it within about 1.5 cm, along the contact; touching its two wires together counts too", seconds);
        else
            log.LogInformation("Done: {Passes} pass(es) in {Seconds} s", passes, seconds);
        return passes > 0 ? 0 : 1;
    }

    /// <summary>
    /// The mailbox HC-SR04 on its own, through the same UltrasonicSensor the mail run uses: four readings a second, each
    /// marked when it's close enough to count as the train (Trackside:HomeSensor:DetectWithinCm), then a summary.
    /// </summary>
    static async Task<int> DistanceAsync(string[] args, SignalBoxOptions options, ILogger log, CancellationToken ct)
    {
        var seconds = args.Length > 0 ? Seconds(args[0]) : 30;
        var o = options.Trackside.HomeSensor;
        if (o.Kind != SensorKind.Ultrasonic)
            throw new ArgumentException($"The mailbox sensor is set to {o.Kind}, not Ultrasonic (Trackside:HomeSensor:Kind)");
        using var gpio = PiGpio.Open();
        var sensor = new UltrasonicSensor(gpio, o);
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromSeconds(seconds));

        log.LogInformation("Mailbox HC-SR04, Trig on GPIO{Trig} and Echo on GPIO{Echo}: readings for {Seconds} s. Closer than {Near} cm counts as the train",
            o.TriggerPin, o.Pin, seconds, o.DetectWithinCm);
        int readings = 0, near = 0, silent = 0;
        double min = double.MaxValue, max = 0;
        try
        {
            while (true)
            {
                if (sensor.MeasureCm() is { } cm)
                {
                    readings++;
                    min = Math.Min(min, cm);
                    max = Math.Max(max, cm);
                    if (cm < o.DetectWithinCm) near++;
                    log.LogInformation(cm < o.DetectWithinCm ? "{Cm,6:0.0} cm   train" : "{Cm,6:0.0} cm", cm);
                }
                else
                {
                    silent++;
                    log.LogInformation("    no echo");
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), window.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The window ended.
        }
        if (readings == 0)
        {
            log.LogWarning("No echo at all in {Seconds} s. Check the Trig wire, the Echo divider, and the sensor's 5 V and ground", seconds);
            return 1;
        }
        log.LogInformation("Done: {Readings} readings from {Min:0.0} to {Max:0.0} cm, {Near} close enough to count as the train, {Silent} with no echo",
            readings, min, max, near, silent);
        return 0;
    }

    /// <summary>
    /// The mailbox flag's SG90 on GPIO18's hardware PWM, moved by GpioTrackside.SetFlag as on a mail run: a short show of
    /// down and up, one position held, or any duty cycle held, to find the right FlagUpDuty and FlagDownDuty.
    /// Each ends with the flag down.
    /// </summary>
    static async Task<int> FlagAsync(string[] args, SignalBoxOptions options, ILoggerFactory loggers, ILogger log, CancellationToken ct)
    {
        var o = options.Trackside;
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "show";
        double? duty = null;
        var seconds = 3.0;
        switch (mode)
        {
            case "show":
                break;
            case "up" or "down":
                seconds = args.Length > 1 ? Seconds(args[1]) : seconds;
                break;
            case "duty":
                duty = args.Length > 1 ? Duty(args[1]) : throw new ArgumentException("Give a duty cycle to hold, for example: bench flag duty 0.06");
                seconds = args.Length > 2 ? Seconds(args[2]) : seconds;
                break;
            default:
                throw new ArgumentException($"Unknown flag command '{args[0]}': use up, down or duty");
        }

        using var track = Open(options, loggers, servo: true);
        if (!track.HasFlag)
        {
            log.LogError("No PWM for the flag servo. Add 'dtoverlay=pwm,pin=18,func=2' to /boot/firmware/config.txt and reboot");
            return 1;
        }
        log.LogInformation("Flag servo on GPIO18: down is duty {Down:0.000} ({DownMs:0.00} ms), up is {Up:0.000} ({UpMs:0.00} ms)",
            o.FlagDownDuty, o.FlagDownDuty * 20, o.FlagUpDuty, o.FlagUpDuty * 20);

        async Task Move(bool up, double hold, string what)
        {
            log.LogInformation("{What}", what);
            track.SetFlag(up);
            await Task.Delay(TimeSpan.FromSeconds(hold), ct);
        }

        var upDuty = o.FlagUpDuty;
        if (mode == "show")
        {
            await Move(false, 1.5, "Flag down");
            await Move(true, 2, "Flag up");
            await Move(false, 2, "Flag down");
            await Move(true, 2, "Flag up");
        }
        else if (duty is { } d)
        {
            // SetFlag(true) moves to FlagUpDuty, so borrow it for the duty being tried.
            o.FlagUpDuty = d;
            await Move(true, seconds, $"Holding duty {d:0.000} ({d * 20:0.00} ms) for {seconds} s");
            o.FlagUpDuty = upDuty;
        }
        else
        {
            await Move(mode == "up", seconds, $"Flag {mode} for {seconds} s");
        }
        await Move(false, 1, "Flag down");
        return 0;
    }

    /// <summary>
    /// The real GPIO trackside, with only the signal and the North Pole sensor, and the flag servo when asked for:
    /// the mailbox sensor stays closed.
    /// </summary>
    static GpioTrackside Open(SignalBoxOptions options, ILoggerFactory loggers, bool servo = false)
    {
        options.Trackside.Mode = TracksideMode.Gpio;
        options.Trackside.ServoEnabled = servo;
        options.Trackside.HomeSensor = new StationSensorOptions { Kind = SensorKind.None };
        return new GpioTrackside(Options.Create(options), loggers.CreateLogger<GpioTrackside>());
    }

    static double Seconds(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 && s <= 600
            ? s
            : throw new ArgumentException($"'{value}' isn't a number of seconds between 0 and 600");

    /// <summary>A servo duty cycle at 50 Hz, kept to 0.4 to 2.6 ms so a typo can't drive the SG90 against its end stops.</summary>
    static double Duty(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0.02 && d <= 0.13
            ? d
            : throw new ArgumentException($"'{value}' isn't a duty cycle between 0.02 and 0.13 (0.4 to 2.6 ms at 50 Hz)");
}
