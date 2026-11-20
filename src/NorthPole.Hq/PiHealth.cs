using System.Globalization;
using System.Text.RegularExpressions;
using static NorthPole.Hq.Processes;

namespace NorthPole.Hq;

/// <summary>The Pi's temperature and whether it has had to slow down, as vcgencmd reports them.</summary>
/// <param name="TempC">The chip's temperature (measure_temp), or null when it couldn't be read.</param>
/// <param name="Throttled">get_throttled's value as vcgencmd prints it, such as 0x50000.</param>
/// <param name="Now">What's happening right now: Under-voltage, Speed capped, Throttled, Soft temp limit.</param>
/// <param name="SinceBoot">The same four, for anything that has happened since the Pi started, now included.</param>
/// <param name="Problem">Why vcgencmd couldn't be read, or null.</param>
public sealed record PiHealthState(double? TempC, string? Throttled, string[] Now, string[] SinceBoot, string? Problem);

/// <summary>
/// Reads the Pi's health for the dashboard's danger zone with vcgencmd, which needs the video group: the Pi's own user
/// has it, and HQ runs as that user.
/// </summary>
static partial class PiHealth
{
    const string Vcgencmd = "/usr/bin/vcgencmd";

    // get_throttled's bits: 0 to 3 are happening now, and 16 to 19 are the same four since the Pi started.
    static readonly string[] Flags = ["Under-voltage", "Speed capped", "Throttled", "Soft temp limit"];

    public static async Task<PiHealthState> ReadAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(Vcgencmd))
            return new(null, null, [], [], "Only HQ on the Pi can read its temperature and throttling.");

        var (tempCode, temp) = await RunAsync(Vcgencmd, ["measure_temp"], ct);
        var (bitsCode, bits) = await RunAsync(Vcgencmd, ["get_throttled"], ct);
        double? tempC = tempCode == 0 && TempPattern().Match(temp) is { Success: true } t
            ? double.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;
        uint? value = bitsCode == 0 && ThrottledPattern().Match(bits) is { Success: true } b
            ? Convert.ToUInt32(b.Groups[1].Value, 16)
            : null;

        string[] Set(int from) => value is { } v ? [.. Flags.Where((_, i) => (v & (1u << (from + i))) != 0)] : [];
        var said = (tempC is null ? temp : value is null ? bits : null)?.Trim();
        var problem = said is null ? null : said.Length > 0 ? $"vcgencmd didn't answer: {said}" : "vcgencmd didn't answer";
        return new(tempC, value is { } x ? $"0x{x:x}" : null, Set(0), Set(16), problem);
    }

    [GeneratedRegex(@"temp=([\d.]+)")]
    private static partial Regex TempPattern();

    [GeneratedRegex(@"throttled=0x([0-9a-fA-F]+)")]
    private static partial Regex ThrottledPattern();
}
