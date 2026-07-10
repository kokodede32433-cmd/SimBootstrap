using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine.Provisioning.Steps;

public class EnsureSshdAutostart : IProvisioningStep
{
    public string Name => "EnsureSshdAutostart";
    public string Description => "Configures the sshd service startup type to Automatic so it starts on Windows boot.";
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
            logs.Add("[Dry-run] Non-Windows environment detected. Simulating sshd autostart check.");
            logs.Add("[Dry-run] Would execute: Set-Service sshd -StartupType Automatic");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("Querying sshd service startup type...");

        var checkResult = await context.CommandRunner.RunPowerShellAsync(
            "(Get-Service sshd).StartType.ToString()",
            TimeSpan.FromSeconds(15),
            cancellationToken);

        if (!checkResult.Succeeded)
        {
            logs.Add($"Failed to query sshd startup type. Error: {checkResult.StdErr}");
            return ProvisioningStepResult.Failure(Name, $"sshd startup type query failed: {checkResult.StdErr}", logs);
        }

        var startType = checkResult.StdOut.Trim();
        logs.Add($"sshd startup type is: '{startType}'");

        if (startType.Equals("Automatic", StringComparison.OrdinalIgnoreCase))
        {
            logs.Add("sshd service is already configured for Automatic startup.");
            return ProvisioningStepResult.Success(Name, logs);
        }

        if (dryRun)
        {
            logs.Add("[Dry-run] Would execute: Set-Service sshd -StartupType Automatic");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("Setting sshd service startup type to Automatic...");
        var setTypeResult = await context.CommandRunner.RunPowerShellAsync(
            "Set-Service sshd -StartupType Automatic",
            TimeSpan.FromSeconds(30),
            cancellationToken);

        if (!setTypeResult.Succeeded)
        {
            logs.Add($"Failed to configure sshd startup type. Error: {setTypeResult.StdErr}");
            return ProvisioningStepResult.Failure(Name, $"Set-Service sshd failed: {setTypeResult.StdErr}", logs);
        }

        // Re-verify
        logs.Add("Verifying sshd service startup type post-config...");
        var verifyResult = await context.CommandRunner.RunPowerShellAsync(
            "(Get-Service sshd).StartType.ToString()",
            TimeSpan.FromSeconds(15),
            cancellationToken);

        if (verifyResult.Succeeded && verifyResult.StdOut.Trim().Equals("Automatic", StringComparison.OrdinalIgnoreCase))
        {
            logs.Add("sshd service startup type successfully configured and verified.");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("sshd service startup configuration completed, but verification still reports it as non-automatic.");
        return ProvisioningStepResult.Failure(Name, "sshd service startup type post-config verification failed.", logs);
    }
}
