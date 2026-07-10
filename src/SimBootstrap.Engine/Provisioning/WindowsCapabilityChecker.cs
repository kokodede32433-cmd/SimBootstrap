using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SimBootstrap.Engine.Provisioning;

public class WindowsCapabilityChecker : IWindowsCapabilityChecker
{
    private readonly ICommandRunner _runner;

    public WindowsCapabilityChecker(ICommandRunner runner)
    {
        _runner = runner;
    }

    public async Task<WindowsCapabilities> CheckCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var capabilities = new WindowsCapabilities();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Fallback for non-Windows platforms (macOS / Linux unit test safety)
            capabilities.WindowsVersion = "Non-Windows (" + RuntimeInformation.OSDescription + ")";
            capabilities.IsAdmin = false;
            capabilities.PowerShellVersion = "None";
            capabilities.IsWinGetAvailable = false;
            return capabilities;
        }

        // 1. Detect OS Version Description
        capabilities.WindowsVersion = RuntimeInformation.OSDescription;

        // 2. Detect Admin privileges using standard identity lookup
        var adminResult = await _runner.RunPowerShellAsync(
            "([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)",
            TimeSpan.FromSeconds(10),
            cancellationToken);
        capabilities.IsAdmin = adminResult.Succeeded && adminResult.StdOut.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);

        // 3. Detect PowerShell Version
        var psResult = await _runner.RunPowerShellAsync(
            "$PSVersionTable.PSVersion.ToString()",
            TimeSpan.FromSeconds(10),
            cancellationToken);
        capabilities.PowerShellVersion = psResult.Succeeded ? psResult.StdOut.Trim() : "Unknown";

        // 4. Detect Winget Availability
        var wingetResult = await _runner.RunPowerShellAsync(
            "Get-Command winget -ErrorAction SilentlyContinue",
            TimeSpan.FromSeconds(10),
            cancellationToken);
        capabilities.IsWinGetAvailable = wingetResult.Succeeded && !string.IsNullOrWhiteSpace(wingetResult.StdOut);

        return capabilities;
    }
}
