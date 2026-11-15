using System.Device.Gpio;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using NorthPole.Contracts;
using NorthPole.Messaging;

namespace NorthPole.SignalBox;

/// <summary>What the station board can see of the line when it draws a screen.</summary>
public readonly record struct LineStatus(bool HqConnected, bool TrainAwake, int? BatteryPercent);

/// <summary>
/// The words on the 16x2 station board at the mailbox. The mail run tells it each stage; it works out the two lines.
/// Anyone in the room can read it, so it shows a first name and where the letter is, never what the letter says.
/// </summary>
public sealed class StationBoard(IOptions<SignalBoxOptions> options, TimeProvider clock)
{
    public const int Width = 16;

    // The LCD's own characters, drawn by CharacterLcd at codes 1 to 3 (code 0 would be a NUL in a string).
    public const char Letter = '\u0001', Engine = '\u0002', Tree = '\u0003';

    static readonly TimeSpan Step = TimeSpan.FromMilliseconds(400); // the little train moves one column
    static readonly TimeSpan Rotate = TimeSpan.FromSeconds(4); // idle lines take turns

    readonly int _lowBattery = options.Value.Train.LowBatteryPercent;
    readonly long _started = clock.GetTimestamp();
    readonly Lock _lock = new();
    Run? _run;
    bool _stopped;

    /// <param name="Since">A monotonic timestamp, so the clock jumping at boot (the Pi has no battery clock) can't upset it.</param>
    sealed record Run(string Child, bool Test, MailStage Stage, long Since);

    /// <summary>Called by the mail run at every stage it reports.</summary>
    public void Show(MailDispatch job, MailStage stage)
    {
        lock (_lock)
        {
            switch (stage)
            {
                case MailStage.Loading or MailStage.Departed or MailStage.Arrived or MailStage.Returning:
                    _run = new Run(job.ChildName, !job.WaitForSanta, stage, clock.GetTimestamp());
                    _stopped = false;
                    break;
                case MailStage.Home:
                    _run = null;
                    _stopped = false; // also after a run the dashboard stopped: back to the idle lines
                    break;
                case MailStage.Failed:
                    _run = null;
                    _stopped = true; // until the next run starts
                    break;
                // Accepted and Rejected: a queued letter waits its turn; a refused one shows as the idle line below.
            }
        }
    }

    public (string Top, string Bottom) Render(LineStatus line)
    {
        Run? run;
        bool stopped;
        lock (_lock)
        {
            run = _run;
            stopped = _stopped;
        }
        if (run is not null)
        {
            var letter = $"{run.Child}'s letter";
            var who = run.Test ? Line("Test run")
                : Line(ToLcd(letter).Length <= Width ? letter : run.Child, Letter); // Charlotte's letter won't fit
            var travelled = clock.GetElapsedTime(run.Since);
            return run.Stage switch
            {
                MailStage.Loading => (who, Line("Loading the post")),
                MailStage.Departed => (who, Moving(travelled, Tree)),
                MailStage.Arrived => (who, run.Test ? Line("At North Pole", Tree) : Line("Santa's reading!")),
                _ => (Line(run.Test ? "Test run: home" : "Letter delivered"), Moving(travelled, null)),
            };
        }

        if (stopped) return (Line("Train stopped"), Line("Grown-up needed"));

        var title = Line("North Pole Mail", Tree);
        if (!line.HqConnected) return (title, Line("HQ not answering"));
        if (!line.TrainAwake) return (title, Line("Train is asleep"));
        if (line.BatteryPercent < _lowBattery) return (title, Line($"Battery low: {line.BatteryPercent}%"));

        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var turn = (int)((long)(clock.GetElapsedTime(_started) / Rotate) % 3);
        return (title, turn switch
        {
            0 => Line("Scan your card!"),
            1 => Line(Sleeps(today)),
            _ => Line(line.BatteryPercent is { } battery ? $"Train ready {battery}%" : "Train ready", Engine),
        });
    }

    static string Sleeps(DateOnly day)
    {
        if (day is { Month: 12, Day: >= 25 }) return "Merry Christmas!";
        var sleeps = new DateOnly(day.Year, 12, 25).DayNumber - day.DayNumber;
        return sleeps == 1 ? "1 sleep to go!" : $"{sleeps} sleeps to go";
    }

    /// <summary>The train runs along the bottom line, round and round: the board can't know how far it has got.</summary>
    static string Moving(TimeSpan travelled, char? destination)
    {
        var track = destination is null ? Width : Width - 1;
        var at = (int)((long)(travelled / Step) % track);
        var row = new string('.', at) + Engine + new string(' ', track - at - 1);
        return destination is { } end ? row + end : row;
    }

    /// <summary>Fits text to one line, with a glyph in the last column if the words leave room for it.</summary>
    static string Line(string text, char? glyph = null)
    {
        var lcd = ToLcd(text);
        if (lcd.Length >= Width) return lcd[..Width]; // the words matter more than the picture
        return glyph is { } g ? lcd.PadRight(Width - 1) + g : lcd.PadRight(Width);
    }

    /// <summary>
    /// The HD44780's character ROM is plain ASCII (except \ and ~), so accents come off (Zoë → Zoe) and a few letters
    /// that have no accent to strip are spelled out (Łucja → Lucja). Anything else becomes ?.
    /// </summary>
    public static string ToLcd(string text)
    {
        var lcd = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (c is >= ' ' and <= '}' and not '\\') lcd.Append(c);
            else if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            else lcd.Append(c switch
            {
                'ł' => "l", 'Ł' => "L", 'đ' => "d", 'Đ' => "D", 'ø' => "o", 'Ø' => "O", 'ß' => "ss",
                'æ' => "ae", 'Æ' => "AE", 'œ' => "oe", 'Œ' => "OE", 'þ' => "th", 'Þ' => "Th", '’' or '‘' => "'",
                _ => "?",
            });
        }
        return lcd.ToString();
    }
}

/// <summary>Somewhere to draw the board: the LCD on the Pi, or the log on a laptop.</summary>
public interface IBoardDisplay : IDisposable
{
    void Show(string top, string bottom);
}

/// <summary>Redraws the board a few times a second, sending the display only what changed.</summary>
public sealed class StationBoardService(StationBoard board, ITrain train, MqttLink mqtt, IServiceProvider services,
    ILogger<StationBoardService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // the LCD takes ~60 ms to start: not on the host's start-up path

        IBoardDisplay display;
        try
        {
            display = services.GetRequiredService<IBoardDisplay>(); // the container disposes it at shutdown
        }
        catch (Exception ex)
        {
            // No board is no reason to stop the trains.
            log.LogWarning(ex, "Station board unavailable: check its wiring and Trackside:Board in appsettings.json");
            return;
        }

        using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        (string, string) shown = default;
        var failing = false;
        do
        {
            var screen = board.Render(new LineStatus(mqtt.IsConnected, train.IsConnected, train.BatteryPercent));
            if (screen == shown) continue;
            try
            {
                display.Show(screen.Top, screen.Bottom);
                shown = screen;
                failing = false;
            }
            catch (Exception ex) when (!failing)
            {
                log.LogWarning(ex, "Station board write failed");
                failing = true;
            }
            catch
            {
                // Already said so; keep trying quietly.
            }
        } while (await tick.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

/// <summary>The JHD162A on the Pi's GPIO, with an envelope, the train and a Christmas tree in its custom characters.</summary>
public sealed class LcdBoardDisplay : IBoardDisplay
{
    readonly GpioController _gpio;
    readonly CharacterLcd _lcd;
    string _top = "", _bottom = "";

    public LcdBoardDisplay(IOptions<SignalBoxOptions> options) : this(options, PiGpio.Open()) { }

    public LcdBoardDisplay(IOptions<SignalBoxOptions> options, GpioController gpio)
    {
        _gpio = gpio;
        var o = options.Value.Trackside.Board;
        _lcd = new CharacterLcd(_gpio, o.RsPin, o.EnablePin, o.D4Pin, o.D5Pin, o.D6Pin, o.D7Pin);
        _lcd.DefineCharacter(StationBoard.Letter, [0x00, 0x1F, 0x1B, 0x15, 0x11, 0x1F, 0x00, 0x00]);
        _lcd.DefineCharacter(StationBoard.Engine, [0x00, 0x1C, 0x16, 0x1F, 0x1F, 0x0A, 0x00, 0x00]); // the 60337's nose
        _lcd.DefineCharacter(StationBoard.Tree, [0x04, 0x0E, 0x1F, 0x0E, 0x1F, 0x04, 0x04, 0x00]);
    }

    public void Show(string top, string bottom)
    {
        if (top != _top) _lcd.Write(0, _top = top);
        if (bottom != _bottom) _lcd.Write(1, _bottom = bottom);
    }

    /// <summary>The Pi's 5 V stays on after a shutdown, and so would the last screen: say the signal box is off.</summary>
    public void Dispose()
    {
        try
        {
            _lcd.Write(0, "Signal box off".PadRight(StationBoard.Width));
            _lcd.Write(1, new string(' ', StationBoard.Width));
        }
        catch { /* shutting down */ }
        _gpio.Dispose();
    }
}

/// <summary>
/// The board in the log, for the laptop. Each line is logged once per screen: the little train moving along gets one
/// line per leg, and the idle lines taking turns one each.
/// </summary>
public sealed class SimulatedBoardDisplay(ILogger<SimulatedBoardDisplay> log) : IBoardDisplay
{
    readonly HashSet<string> _seen = [];
    string _top = "";
    bool _moving;

    public void Show(string top, string bottom)
    {
        var moving = bottom.All(c => c is '.' or ' ' or StationBoard.Engine or StationBoard.Tree);
        if (top != _top) _seen.Clear();
        var repeat = moving ? _moving && top == _top : !_seen.Add(bottom);
        (_top, _moving) = (top, moving);
        if (!repeat) log.LogInformation("(sim) Board [{Top}] [{Bottom}]", Ascii(top), Ascii(bottom));
    }

    static string Ascii(string line) =>
        line.Replace(StationBoard.Letter, '@').Replace(StationBoard.Engine, '>').Replace(StationBoard.Tree, '*');

    public void Dispose() { }
}

/// <summary>
/// A character LCD with an HD44780 controller, wired in 4-bit mode with RW tied to ground: the Pi only writes, so the
/// LCD's 5 V never reaches a GPIO pin, and 3.3 V is high enough for its inputs. The start-up and timings follow the
/// datasheet, the way the .NET IoT Hd44780 binding does, without pulling in the whole bindings package.
/// </summary>
public sealed class CharacterLcd
{
    static readonly TimeSpan PinSettle = TimeSpan.FromMicroseconds(1); // enable pulse: 450 ns minimum at 3 V
    static readonly TimeSpan CommandTime = TimeSpan.FromMicroseconds(50); // most instructions take 37 µs
    static readonly TimeSpan ClearTime = TimeSpan.FromMilliseconds(2); // clear takes 1.52 ms

    static readonly byte[] RowStart = [0x00, 0x40];

    readonly GpioController _gpio;
    readonly int _rs, _enable;
    readonly int[] _data;

    public CharacterLcd(GpioController gpio, int rs, int enable, int d4, int d5, int d6, int d7)
    {
        _gpio = gpio;
        _rs = rs;
        _enable = enable;
        _data = [d4, d5, d6, d7];
        int[] pins = [rs, enable, .. _data];
        foreach (var pin in pins) _gpio.OpenPin(pin, PinMode.Output, PinValue.Low);

        // Whatever state the controller woke up in, three 8-bit "function set"s bring it to 8-bit mode; then switch
        // to 4-bit. Each is half a byte, since only D4 to D7 are wired.
        Thread.Sleep(50); // more than 40 ms after power reaches 2.7 V
        WriteNibble(0x3, data: false);
        Thread.Sleep(5);
        WriteNibble(0x3, data: false);
        Wait(TimeSpan.FromMicroseconds(150));
        WriteNibble(0x3, data: false);
        Wait(TimeSpan.FromMicroseconds(150));
        WriteNibble(0x2, data: false);
        Wait(CommandTime);

        Command(0x28); // function set: 4-bit, two lines, 5x8 dots
        Command(0x08); // display off
        Clear();
        Command(0x06); // entry mode: the cursor moves right, the display stays put
        Command(0x0C); // display on, no cursor, no blink
    }

    public void Clear()
    {
        Command(0x01);
        Wait(ClearTime);
    }

    /// <summary>Writes a whole line from its first column. Characters 1 to 7 are the custom ones.</summary>
    public void Write(int row, string text)
    {
        Command((byte)(0x80 | RowStart[row]));
        foreach (var c in text) Data((byte)c);
    }

    /// <summary>One of the eight custom characters: 8 rows of 5 dots, top first, low 5 bits of each byte.</summary>
    public void DefineCharacter(int code, ReadOnlySpan<byte> rows)
    {
        if (rows.Length != 8) throw new ArgumentException("A character is 8 rows", nameof(rows));
        Command((byte)(0x40 | (code & 7) << 3));
        foreach (var row in rows) Data(row);
        Command(0x80); // back to the display memory
    }

    void Command(byte value) => WriteByte(value, data: false);

    void Data(byte value) => WriteByte(value, data: true);

    void WriteByte(byte value, bool data)
    {
        WriteNibble(value >> 4, data);
        WriteNibble(value & 0xF, data);
        Wait(CommandTime);
    }

    void WriteNibble(int nibble, bool data)
    {
        _gpio.Write(_rs, data);
        for (var bit = 0; bit < 4; bit++) _gpio.Write(_data[bit], (nibble >> bit & 1) == 1);
        Wait(PinSettle);
        _gpio.Write(_enable, PinValue.High);
        Wait(PinSettle);
        _gpio.Write(_enable, PinValue.Low); // the LCD reads RS and D4 to D7 on this falling edge
        Wait(PinSettle);
    }

    static void Wait(TimeSpan time)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < time) { } // microseconds: too short to sleep, spin
    }
}
