using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine.Provisioning.Steps;

public class EnsureFirewallRuleForSsh : IProvisioningStep
{
    public string Name => "EnsureFirewallRuleForSsh";
    public string Description => "Opens inbound port 22 in Windows Firewall for SSH connections.";
    public bool IsCritical => true;

    public Task<bool> ShouldRunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult(context.Config.ConfigureFirewall);
    }

    public async Task<ProvisioningStepResult> ExecuteAsync(ProvisioningContext context, bool dryRun, CancellationToken cancellationToken)
    {
        var logs = new List<string>();
        
        if (dryRun && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            logs.Add("[Dry-run] Non-Windows environment detected. Simulating firewall rule check.");
            logs.Add("[Dry-run] Would execute: New-NetFirewallRule -Name \"SSH\" -DisplayName \"Allow SSH\" -Profile Any -Direction Inbound -Action Allow -Protocol TCP -LocalPort 22");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("Checking if firewall rule for port 22 already exists...");

        var checkResult = await context.CommandRunner.RunPowerShellAsync(
            "Get-NetFirewallRule -Name \"SSH\" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name",
            TimeSpan.FromSeconds(15),
            cancellationToken);

        if (checkResult.Succeeded && !string.IsNullOrWhiteSpace(checkResult.StdOut))
        {
            logs.Add("Firewall rule 'SSH' already exists.");
            return ProvisioningStepResult.Success(Name, logs);
        }

        // Also check default rule that comes with OpenSSH Server package
        var defaultRuleResult = await context.CommandRunner.RunPowerShellAsync(
            "Get-NetFirewallRule -Name \"OpenSSH-Server-In-TCP\" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Enabled",
            TimeSpan.FromSeconds(15),
            cancellationToken);

        if (defaultRuleResult.Succeeded && defaultRuleResult.StdOut.Trim().Equals("True", StringComparison.OrdinalIgnoreCase))
        {
            logs.Add("Default OpenSSH Server firewall rule 'OpenSSH-Server-In-TCP' exists and is enabled.");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("Inbound SSH firewall rule is not configured or enabled.");

        if (dryRun)
        {
            logs.Add("[Dry-run] Would execute: New-NetFirewallRule -Name \"SSH\" -DisplayName \"Allow SSH\" -Profile Any -Direction Inbound -Action Allow -Protocol TCP -LocalPort 22");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("Creating inbound firewall rule for Port 22...");
        var createResult = await context.CommandRunner.RunPowerShellAsync(
            "New-NetFirewallRule -Name \"SSH\" -DisplayName \"Allow SSH\" -Profile Any -Direction Inbound -Action Allow -Protocol TCP -LocalPort 22",
            TimeSpan.FromSeconds(30),
            cancellationToken);

        if (!createResult.Succeeded)
        {
            logs.Add($"Failed to create firewall rule. Error: {createResult.StdErr}");
            return ProvisioningStepResult.Failure(Name, $"New-NetFirewallRule failed: {createResult.StdErr}", logs);
        }

        // Re-verify
        logs.Add("Verifying firewall rule creation...");
        var verifyResult = await context.CommandRunner.RunPowerShellAsync(
            "Get-NetFirewallRule -Name \"SSH\" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name",
            TimeSpan.FromSeconds(15),
            cancellationToken);

        if (verifyResult.Succeeded && !string.IsNullOrWhiteSpace(verifyResult.StdOut))
        {
            logs.Add("Firewall rule 'SSH' successfully created and verified.");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("Firewall rule creation reported success, but status verification failed.");
        return ProvisioningStepResult.Failure(Name, "Firewall rule post-creation verification failed.", logs);
    }
}
