using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimBootstrap.Engine.Provisioning;

public class PowerShellRunner : ICommandRunner
{
    public async Task<CommandResult> RunPowerShellAsync(string command, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var runTimeout = timeout ?? TimeSpan.FromMinutes(5);
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var shellFilename = isWindows ? "powershell.exe" : "pwsh";

        // Escape double quotes inside the command string for PowerShell CLI arguments
        var escapedCommand = command.Replace("\"", "\\\"");
        var arguments = $"-NoProfile -NonInteractive -Command \"{escapedCommand}\"";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = shellFilename,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        try
        {
            if (!process.Start())
            {
                return new CommandResult(-1, string.Empty, $"Failed to start process {shellFilename}");
            }
        }
        catch (Exception ex)
        {
            return new CommandResult(-1, string.Empty, $"Failed to start shell process: {ex.Message}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        
        // Start reading streams asynchronously
        var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var errorTask = process.StandardError.ReadToEndAsync(cts.Token);
        var processTask = process.WaitForExitAsync(cts.Token);

        var delayTask = Task.Delay(runTimeout, cts.Token);
        var completedTask = await Task.WhenAny(processTask, delayTask);

        if (completedTask == delayTask)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                try { process.Kill(); } catch { }
            }
            cts.Cancel();
            return new CommandResult(-2, string.Empty, $"Execution timed out after {runTimeout.TotalSeconds} seconds.");
        }

        // Wait for asynchronous reading to finish
        await Task.WhenAll(outputTask, errorTask);
        return new CommandResult(process.ExitCode, outputTask.Result.Trim(), errorTask.Result.Trim());
    }
}
