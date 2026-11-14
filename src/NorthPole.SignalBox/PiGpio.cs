using System.Device.Gpio;
using System.Device.Gpio.Drivers;
using System.Runtime.InteropServices;

namespace NorthPole.SignalBox;

/// <summary>
/// Opens the Pi's GPIO header. The GPIO library's default driver loads libgpiod v1 (libgpiod.so.2), but Raspberry Pi OS
/// 13 (trixie) ships only libgpiod.so.3, which only the library's V2 driver uses. So where libgpiod.so.3 is installed,
/// the V2 driver is chosen here, on the chip that carries the header: pinctrl-rp1 on a Pi 5, pinctrl-bcm2711 on a Pi 4.
/// Elsewhere (Bookworm, with libgpiod.so.2) the library picks its own driver, as before.
/// </summary>
public static class PiGpio
{
    public static GpioController Open()
    {
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libgpiod.so.3", out var library))
            return new GpioController();
        NativeLibrary.Free(library);
        return new GpioController(new LibGpiodV2Driver(HeaderChip()));
    }

    /// <summary>The chip named after the SoC's pin controller. On recent kernels that is chip 0, the fallback.</summary>
    static int HeaderChip() =>
        LibGpiodV2Driver.GetAvailableChips()
            .FirstOrDefault(c => c.Label.StartsWith("pinctrl-rp1", StringComparison.Ordinal)
                                 || c.Label.StartsWith("pinctrl-bcm", StringComparison.Ordinal))
            ?.Id ?? 0;
}
