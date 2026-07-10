using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine.Provisioning.Steps;

public class EnsureAuthorizedKeys : IProvisioningStep
{
    public string Name => "EnsureAuthorizedKeys";
    public string Description => "Configures the public SSH key in the user's authorized_keys file and secures permissions.";
    public bool IsCritical => true;

    public Task<bool> ShouldRunAsync(ProvisioningContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }

    public async Task<ProvisioningStepResult> ExecuteAsync(ProvisioningContext context, bool dryRun, CancellationToken cancellationToken)
    {
        var logs = new List<string>();
        var targetKey = context.Config.AuthorizedPublicKey.Trim();
        
        if (dryRun)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                logs.Add("[Dry-run] Non-Windows environment detected. Simulating authorized_keys key injection.");
            }
            else
            {
                logs.Add("[Dry-run] Simulating authorized_keys key injection.");
            }
            logs.Add("[Dry-run] Would check whether the public key is present in authorized_keys.");
            logs.Add($"[Dry-run] Would append public key to authorized_keys and apply security ACLs: {targetKey}");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("Verifying if the public key is present in authorized_keys...");

        // Script to check if public key is already in C:\Users\<user>\.ssh\authorized_keys
        var checkScript = 
            "$authKeys = Join-Path (Join-Path $env:USERPROFILE \".ssh\") \"authorized_keys\"; " +
            "if (Test-Path $authKeys) { " +
            "  $content = Get-Content $authKeys -ErrorAction SilentlyContinue; " +
            "  if ($content -ne $null) { " +
            "    $found = $false; " +
            "    foreach ($line in $content) { " +
            "      if ($line.Trim() -eq \"" + targetKey + "\") { $found = $true; break; } " +
            "    }; " +
            "    if ($found) { Write-Output \"Exists\" } else { Write-Output \"Missing\" } " +
            "  } else { Write-Output \"Missing\" } " +
            "} else { Write-Output \"Missing\" }";

        var checkResult = await context.CommandRunner.RunPowerShellAsync(checkScript, TimeSpan.FromSeconds(20), cancellationToken);
        if (checkResult.Succeeded && checkResult.StdOut.Trim().Equals("Exists", StringComparison.OrdinalIgnoreCase))
        {
            logs.Add("Public key is already present in authorized_keys.");
            return ProvisioningStepResult.Success(Name, logs);
        }

        logs.Add("Public key is missing from authorized_keys.");

        logs.Add("Writing public key and securing authorized_keys permissions...");

        var applyScript =
            "$sshDir = Join-Path $env:USERPROFILE \".ssh\"; " +
            "if (!(Test-Path $sshDir)) { New-Item -ItemType Directory -Path $sshDir -Force | Out-Null }; " +
            "$authKeys = Join-Path $sshDir \"authorized_keys\"; " +
            "Add-Content -Path $authKeys -Value \"" + targetKey + "\" -Force; " +
            " " +
            "try { " +
            "  $acl = Get-Acl $authKeys; " +
            "  $acl.SetAccessRuleProtection($true, $false); " +
            "  $rules = $acl.GetAccessRules($true, $true, [System.Security.Principal.NTAccount]); " +
            "  foreach ($rule in $rules) { $acl.RemoveAccessRule($rule) | Out-Null }; " +
            " " +
            "  $userAccount = [System.Security.Principal.NTAccount]($env:USERNAME); " +
            "  $systemAccount = [System.Security.Principal.NTAccount](\"NT AUTHORITY\\SYSTEM\"); " +
            "  $adminAccount = [System.Security.Principal.NTAccount](\"BUILTIN\\Administrators\"); " +
            " " +
            "  $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($userAccount, \"FullControl\", \"Allow\"))); " +
            "  $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($systemAccount, \"FullControl\", \"Allow\"))); " +
            "  $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($adminAccount, \"FullControl\", \"Allow\"))); " +
            " " +
            "  Set-Acl $authKeys $acl; " +
            "  Write-Output \"Success\" " +
            "} catch { " +
            "  Write-Error $_.Exception.Message " +
            "}";

        var applyResult = await context.CommandRunner.RunPowerShellAsync(applyScript, TimeSpan.FromSeconds(30), cancellationToken);

        if (!applyResult.Succeeded)
        {
            logs.Add($"Failed to configure authorized_keys. Error: {applyResult.StdErr}");
            return ProvisioningStepResult.Failure(Name, $"authorized_keys configuration failed: {applyResult.StdErr}", logs);
        }

        logs.Add("authorized_keys configured and secured successfully.");
        return ProvisioningStepResult.Success(Name, logs);
    }
}
