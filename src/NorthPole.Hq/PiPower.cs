using NorthPole.Contracts;
using NorthPole.Hq.Mail;
using static NorthPole.Hq.Processes;

namespace NorthPole.Hq;

public enum PowerAction { Reboot, Shutdown }

/// <summary>
/// The dashboard's danger zone: reboots or shuts down the Pi that HQ runs on. HQ runs as the Pi's own user, so it asks
/// sudo, which setup.sh allows for exactly these two commands and nothing else (/etc/sudoers.d/northpole-power). The
/// train lets go of the hub first, and the Pi goes a few seconds later, so the phone gets its answer before HQ stops.
/// </summary>
public sealed class PiPower(MailTrainClient train, ILogger<PiPower> log)
{
    const string Systemctl = "/usr/bin/systemctl";
    int _going;

    /// <summary>Null when the Pi is on its way, or why it can't go.</summary>
    public async Task<string?> RequestAsync(PowerAction action, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return "Only HQ on the Pi can do that.";
        var verb = action == PowerAction.Reboot ? "reboot" : "poweroff";

        // sudo -l with a command says whether it's allowed without running it; -n never waits for a password.
        var (code, output) = await RunAsync("sudo", ["-n", "-l", Systemctl, verb], ct);
        if (code != 0)
        {
            log.LogWarning("The elves asked the Pi to {Verb}, but sudo won't let HQ: {Output}", verb, output.Trim());
            return "HQ isn't allowed to do that yet. Run setup.sh on the Pi again: it adds the rule.";
        }
        if (Interlocked.Exchange(ref _going, 1) == 1) return null; // a second tap: it's already on its way

        log.LogWarning("The elves asked the Pi to {Verb}", verb);
        _ = Task.Run(async () =>
        {
            await train.SendCommandAsync(TrainCommandKind.Disconnect); // brakes, ends any run, lets the hub go
            await Task.Delay(TimeSpan.FromSeconds(3));
            var (failed, why) = await RunAsync("sudo", ["-n", Systemctl, verb], CancellationToken.None);
            if (failed == 0) return;
            log.LogError("sudo systemctl {Verb} failed ({Code}): {Output}", verb, failed, why.Trim());
            Volatile.Write(ref _going, 0);
        }, CancellationToken.None);
        return null;
    }
}
