using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
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

        logs.Add("Configuring authorized_keys and SSH file permissions...");

        var encodedKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(targetKey));
        var applyScript = """
$ErrorActionPreference = 'Stop'
$targetKey = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__AUTHORIZED_KEY_BASE64__')).Trim()
$sshDir = Join-Path $env:USERPROFILE ".ssh"
if (!(Test-Path -LiteralPath $sshDir)) {
  New-Item -ItemType Directory -Path $sshDir -Force | Out-Null
}
$authKeys = Join-Path $sshDir "authorized_keys"
if (!(Test-Path -LiteralPath $authKeys)) {
  New-Item -ItemType File -Path $authKeys -Force | Out-Null
}
if (!(Test-Path -LiteralPath $authKeys)) {
  throw "authorized_keys path was not created: $authKeys"
}

$content = @(Get-Content -LiteralPath $authKeys -ErrorAction SilentlyContinue)
$found = $false
foreach ($line in $content) {
  if ($line.Trim() -eq $targetKey) {
    $found = $true
    break
  }
}
if ($found) {
  Write-Output "KeyAlreadyPresent"
} else {
  Add-Content -LiteralPath $authKeys -Value $targetKey -Encoding utf8
  Write-Output "KeyAdded"
}

try {
  $acl = Get-Acl -LiteralPath $authKeys -ErrorAction Stop
  if ($null -eq $acl) {
    throw "Get-Acl returned no ACL for $authKeys"
  }
  $acl.SetAccessRuleProtection($true, $false)
  $rules = $acl.GetAccessRules($true, $true, [System.Security.Principal.NTAccount])
  foreach ($rule in $rules) {
    $acl.RemoveAccessRule($rule) | Out-Null
  }

  $userAccount = [System.Security.Principal.NTAccount]($env:USERNAME)
  $systemAccount = [System.Security.Principal.NTAccount]("NT AUTHORITY\SYSTEM")
  $adminAccount = [System.Security.Principal.NTAccount]("BUILTIN\Administrators")

  $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($userAccount, "FullControl", "Allow")))
  $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($systemAccount, "FullControl", "Allow")))
  $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($adminAccount, "FullControl", "Allow")))

  Set-Acl -LiteralPath $authKeys -AclObject $acl -ErrorAction Stop
  Write-Output "AclApplied"
} catch {
  Write-Error ("ACL configuration failed: " + $_.Exception.Message)
  exit 1
}
""".Replace("__AUTHORIZED_KEY_BASE64__", encodedKey, StringComparison.Ordinal);

        var applyResult = await context.CommandRunner.RunPowerShellAsync(applyScript, TimeSpan.FromSeconds(30), cancellationToken);

        if (!applyResult.Succeeded)
        {
            logs.Add($"Failed to configure authorized_keys. Error: {applyResult.StdErr}");
            return ProvisioningStepResult.Failure(Name, $"authorized_keys configuration failed: {applyResult.StdErr}", logs);
        }

        if (applyResult.StdOut.Contains("KeyAlreadyPresent", StringComparison.OrdinalIgnoreCase))
        {
            logs.Add("Public key is already present in authorized_keys.");
        }
        else if (applyResult.StdOut.Contains("KeyAdded", StringComparison.OrdinalIgnoreCase))
        {
            logs.Add("Public key added to authorized_keys.");
        }

        logs.Add("authorized_keys configured and secured successfully.");
        return ProvisioningStepResult.Success(Name, logs);
    }
}
