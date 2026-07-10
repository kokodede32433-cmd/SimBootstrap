using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine.Provisioning.Steps;

public class EnsureOpenSshServerInstalled : IProvisioningStep
{
    public string Name => "EnsureOpenSshServerInstalled";
    public string Description => "Checks if the OpenSSH Server capability is installed on Windows, and installs it if missing.";
    public bool IsCritical => true;

    public Task<bool> ShouldRunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult(context.Config.ConfigureOpenSsh);
    }

    public async Task<ProvisioningStepResult> ExecuteAsync(ProvisioningContext context, bool dryRun, CancellationToken cancellationToken)
    {
        var logs = new List<string>();
        
        if (dryRun && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            logs.Add("[Dry-run] Non-Windows environment detected. Simulating OpenSSH Server capability check.");
            logs.Add("[Dry-run] Would execute: Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("Checking OpenSSH.Server Windows capability state...");

        var checkResult = await context.CommandRunner.RunPowerShellAsync(
            "Get-WindowsCapability -Online -Name OpenSSH.Server*",
            TimeSpan.FromSeconds(30),
            cancellationToken);

        if (!checkResult.Succeeded)
        {
            logs.Add($"Failed to query Windows capabilities: {checkResult.StdErr}");
            return ProvisioningStepResult.Failure(Name, $"Get-WindowsCapability failed: {checkResult.StdErr}", logs);
        }

        logs.Add($"Capability info:\n{checkResult.StdOut}");

        if (checkResult.StdOut.Contains("State : Installed", StringComparison.OrdinalIgnoreCase))
        {
            logs.Add("OpenSSH Server is already installed.");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("OpenSSH Server is not installed.");

        if (dryRun)
        {
            logs.Add("[Dry-run] Would execute: Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("Installing OpenSSH Server capability (this may take a few minutes)...");
        var installResult = await context.CommandRunner.RunPowerShellAsync(
            "Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0",
            TimeSpan.FromMinutes(10),
            cancellationToken);

        if (!installResult.Succeeded)
        {
            logs.Add($"Failed to install OpenSSH Server capability. Error: {installResult.StdErr}");
            return ProvisioningStepResult.Failure(Name, $"Add-WindowsCapability failed: {installResult.StdErr}", logs);
        }

        logs.Add("OpenSSH Server capability installation completed. Verifying status...");
        var verifyResult = await context.CommandRunner.RunPowerShellAsync(
            "Get-WindowsCapability -Online -Name OpenSSH.Server*",
            TimeSpan.FromSeconds(30),
            cancellationToken);

        if (verifyResult.Succeeded && verifyResult.StdOut.Contains("State : Installed", StringComparison.OrdinalIgnoreCase))
        {
            logs.Add("OpenSSH Server successfully installed and verified.");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("OpenSSH Server installation returned success, but verification still reports it as missing.");
        return ProvisioningStepResult.Failure(Name, "OpenSSH Server capability verification failed post-install.", logs);
    }
}
