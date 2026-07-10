using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine.Provisioning.Steps;

public class EnsureGitInstalled : IProvisioningStep
{
    public string Name => "EnsureGitInstalled";
    public string Description => "Checks if Git is installed, and installs it via Winget if missing and allowed by configuration.";
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
            logs.Add("[Dry-run] Non-Windows environment detected. Simulating Git installation check.");
            logs.Add("[Dry-run] Would execute: winget install --id Git.Git --silent --accept-source-agreements --accept-package-agreements");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("Checking if Git is installed...");

        var checkResult = await context.CommandRunner.RunPowerShellAsync("git --version", TimeSpan.FromSeconds(15), cancellationToken);
        if (checkResult.Succeeded)
        {
            logs.Add($"Git is already installed: {checkResult.StdOut}");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("Git is not found in PATH.");

        if (!context.Config.InstallGit)
        {
            logs.Add("Git installation is disabled by configuration.");
            return ProvisioningStepResult.Failure(Name, "Git is not installed, and installGit is disabled in configuration.", logs);
        }

        if (!context.Capabilities.IsWinGetAvailable)
        {
            logs.Add("Error: WinGet is not available on this system. Cannot install Git automatically.");
            return ProvisioningStepResult.Failure(Name, "WinGet is not available, unable to install Git.", logs);
        }

        if (dryRun)
        {
            logs.Add("[Dry-run] Would execute: winget install --id Git.Git --silent --accept-source-agreements --accept-package-agreements");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("Installing Git via WinGet...");
        var installResult = await context.CommandRunner.RunPowerShellAsync(
            "winget install --id Git.Git --silent --accept-source-agreements --accept-package-agreements",
            TimeSpan.FromMinutes(5),
            cancellationToken);

        if (!installResult.Succeeded)
        {
            logs.Add($"Git installation failed (Exit code: {installResult.ExitCode}). Error: {installResult.StdErr}");
            return ProvisioningStepResult.Failure(Name, $"Winget installation failed: {installResult.StdErr}", logs);
        }

        // Re-verify after install
        logs.Add("Verifying Git installation...");
        var verifyResult = await context.CommandRunner.RunPowerShellAsync("git --version", TimeSpan.FromSeconds(15), cancellationToken);
        if (verifyResult.Succeeded)
        {
            logs.Add($"Git successfully installed and verified: {verifyResult.StdOut}");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("Git was installed, but command verification failed. System restart or environment variables refresh might be needed.");
        return ProvisioningStepResult.Failure(Name, "Git installation completed but command verification failed.", logs);
    }
}
