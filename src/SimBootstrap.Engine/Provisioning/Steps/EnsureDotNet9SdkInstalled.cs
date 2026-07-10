using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine.Provisioning.Steps;

public class EnsureDotNet9SdkInstalled : IProvisioningStep
{
    public string Name => "EnsureDotNet9SdkInstalled";
    public string Description => "Checks if .NET 9 SDK is installed, and installs it via Winget if missing and allowed by configuration.";
    public bool IsCritical => true;

    public Task<bool> ShouldRunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }

    public async Task<ProvisioningStepResult> ExecuteAsync(ProvisioningContext context, bool dryRun, CancellationToken cancellationToken)
    {
        var logs = new List<string>();
        
        if (dryRun && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            logs.Add("[Dry-run] Non-Windows environment detected. Simulating .NET 9 SDK installation check.");
            logs.Add("[Dry-run] Would execute: winget install --id Microsoft.DotNet.SDK.9 --silent --accept-source-agreements --accept-package-agreements");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("Checking if .NET 9 SDK is installed...");

        var checkResult = await context.CommandRunner.RunPowerShellAsync("dotnet --list-sdks", TimeSpan.FromSeconds(15), cancellationToken);
        if (checkResult.Succeeded && checkResult.StdOut.Contains("9.0.", StringComparison.OrdinalIgnoreCase))
        {
            logs.Add($".NET 9 SDK is already installed:\n{checkResult.StdOut}");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add(".NET 9 SDK is not found.");

        if (!context.Config.InstallDotNet9)
        {
            logs.Add(".NET 9 SDK installation is disabled by configuration.");
            return ProvisioningStepResult.Failure(Name, ".NET 9 SDK is not installed, and installDotNet9 is disabled in configuration.", logs);
        }

        if (!context.Capabilities.IsWinGetAvailable)
        {
            logs.Add("Error: WinGet is not available on this system. Cannot install .NET 9 SDK automatically.");
            return ProvisioningStepResult.Failure(Name, "WinGet is not available, unable to install .NET 9 SDK.", logs);
        }

        if (dryRun)
        {
            logs.Add("[Dry-run] Would execute: winget install --id Microsoft.DotNet.SDK.9 --silent --accept-source-agreements --accept-package-agreements");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("Installing .NET 9 SDK via WinGet...");
        var installResult = await context.CommandRunner.RunPowerShellAsync(
            "winget install --id Microsoft.DotNet.SDK.9 --silent --accept-source-agreements --accept-package-agreements",
            TimeSpan.FromMinutes(8),
            cancellationToken);

        if (!installResult.Succeeded)
        {
            logs.Add($".NET 9 SDK installation failed (Exit code: {installResult.ExitCode}). Error: {installResult.StdErr}");
            return ProvisioningStepResult.Failure(Name, $".NET 9 SDK installation failed: {installResult.StdErr}", logs);
        }

        // Re-verify after install
        logs.Add("Verifying .NET 9 SDK installation...");
        var verifyResult = await context.CommandRunner.RunPowerShellAsync("dotnet --list-sdks", TimeSpan.FromSeconds(15), cancellationToken);
        if (verifyResult.Succeeded && verifyResult.StdOut.Contains("9.0.", StringComparison.OrdinalIgnoreCase))
        {
            logs.Add(".NET 9 SDK successfully installed and verified.");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add(".NET 9 SDK was installed, but command verification failed. System restart or environment variables refresh might be needed.");
        return ProvisioningStepResult.Failure(Name, ".NET 9 SDK installation completed but command verification failed.", logs);
    }
}
