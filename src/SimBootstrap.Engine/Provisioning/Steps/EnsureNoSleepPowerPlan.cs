using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine.Provisioning.Steps;

public class EnsureNoSleepPowerPlan : IProvisioningStep
{
    public string Name => "EnsureNoSleepPowerPlan";
    public string Description => "Disables sleep and standby timeouts to prevent the PC from going offline.";
    public bool IsCritical => false;

    public Task<bool> ShouldRunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult(context.Config.DisableSleep);
    }

    public async Task<ProvisioningStepResult> ExecuteAsync(ProvisioningContext context, bool dryRun, CancellationToken cancellationToken)
    {
        var logs = new List<string>();
        
        if (dryRun)
        {
            logs.Add("[Dry-run] Would execute: powercfg /change standby-timeout-ac 0");
            logs.Add("[Dry-run] Would execute: powercfg /change standby-timeout-dc 0");
            logs.Add("[Dry-run] Would execute: powercfg /change monitor-timeout-ac 0");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("Disabling system standby when on AC power...");
        var standbyAcResult = await context.CommandRunner.RunPowerShellAsync("powercfg /change standby-timeout-ac 0", TimeSpan.FromSeconds(15), cancellationToken);
        if (!standbyAcResult.Succeeded)
        {
            logs.Add($"Warning: powercfg standby-timeout-ac failed: {standbyAcResult.StdErr}");
        }

        logs.Add("Disabling system standby when on battery power...");
        var standbyDcResult = await context.CommandRunner.RunPowerShellAsync("powercfg /change standby-timeout-dc 0", TimeSpan.FromSeconds(15), cancellationToken);
        if (!standbyDcResult.Succeeded)
        {
            logs.Add($"Warning: powercfg standby-timeout-dc failed: {standbyDcResult.StdErr}");
        }

        logs.Add("Disabling monitor sleep when on AC power...");
        var monitorAcResult = await context.CommandRunner.RunPowerShellAsync("powercfg /change monitor-timeout-ac 0", TimeSpan.FromSeconds(15), cancellationToken);
        if (!monitorAcResult.Succeeded)
        {
            logs.Add($"Warning: powercfg monitor-timeout-ac failed: {monitorAcResult.StdErr}");
        }

        logs.Add("Power plan settings configured successfully.");
        return ProvisioningStepResult.Success(Name, logs);
    }
}
