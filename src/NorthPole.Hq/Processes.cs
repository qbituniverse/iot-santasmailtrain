using System.Diagnostics;

namespace NorthPole.Hq;

/// <summary>Runs a program on the Pi, such as amixer or sudo, and hands back its exit code and what it printed.</summary>
static class Processes
{
    public static async Task<(int Code, string Output)> RunAsync(string command, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(command) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return (-1, $"{command} didn't start");
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, await stderr + await stdout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (-1, ex.Message);
        }
    }
}
