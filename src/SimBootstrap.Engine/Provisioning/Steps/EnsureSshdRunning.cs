using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine.Provisioning.Steps;

public class EnsureSshdRunning : IProvisioningStep
{
    public string Name => "EnsureSshdRunning";
    public string Description => "Checks if the sshd service is running on Windows, and starts it if it is stopped.";
    public bool IsCritical => true;

    public Task<bool> ShouldRunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult(context.Config.ConfigureOpenSsh);
    }

    public async Task<ProvisioningStepResult> ExecuteAsync(ProvisioningContext context, bool dryRun, CancellationToken cancellationToken)
    {
        var logs = new List<string>();
        
        if (dryRun)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                logs.Add("[Dry-run] Non-Windows environment detected. Simulating sshd status check.");
            }
            else
            {
                logs.Add("[Dry-run] Simulating sshd status check.");
            }
            logs.Add("[Dry-run] Would execute: (Get-Service sshd).Status.ToString()");
            logs.Add("[Dry-run] Would execute: Start-Service sshd");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("Querying sshd service status...");

        var checkResult = await context.CommandRunner.RunPowerShellAsync(
            "(Get-Service sshd).Status.ToString()",
            TimeSpan.FromSeconds(15),
            cancellationToken);

        if (!checkResult.Succeeded)
        {
            logs.Add($"Failed to query sshd service status. Error: {checkResult.StdErr}");
            return ProvisioningStepResult.Failure(Name, $"sshd service query failed: {checkResult.StdErr}", logs);
        }

        var status = checkResult.StdOut.Trim();
        logs.Add($"sshd service status is: '{status}'");

        if (status.Equals("Running", StringComparison.OrdinalIgnoreCase))
        {
            logs.Add("sshd service is already running.");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("Starting sshd service...");
        var startResult = await context.CommandRunner.RunPowerShellAsync("Start-Service sshd", TimeSpan.FromSeconds(30), cancellationToken);

        if (!startResult.Succeeded)
        {
            logs.Add($"Failed to start sshd service. Error: {startResult.StdErr}");
            return ProvisioningStepResult.Failure(Name, $"Start-Service sshd failed: {startResult.StdErr}", logs);
        }

        // Re-verify
        logs.Add("Verifying sshd service status post-start...");
        var verifyResult = await context.CommandRunner.RunPowerShellAsync(
            "(Get-Service sshd).Status.ToString()",
            TimeSpan.FromSeconds(15),
            cancellationToken);

        if (verifyResult.Succeeded && verifyResult.StdOut.Trim().Equals("Running", StringComparison.OrdinalIgnoreCase))
        {
            logs.Add("sshd service successfully started and verified.");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("sshd service start reported success, but status is still not Running.");
        return ProvisioningStepResult.Failure(Name, "sshd service status post-start verification failed.", logs);
    }
}
