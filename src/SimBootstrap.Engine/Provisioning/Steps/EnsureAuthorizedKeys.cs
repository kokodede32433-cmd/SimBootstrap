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

function Repair-PathAccess {
  param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$Description
  )

  $takeownArgs = @('/F', $Path, '/A')
  & takeown @takeownArgs | Out-Null
  if ($LASTEXITCODE -ne 0) {
    throw "$Description exists but permissions could not be repaired"
  }

  $userGrant = "$env:USERNAME:F"
  & icacls $Path /grant $userGrant /grant "SYSTEM:F" /grant "Administrators:F" | Out-Null
  if ($LASTEXITCODE -ne 0) {
    throw "$Description exists but permissions could not be repaired"
  }

  Write-Output "PermissionsRepaired:$Description"
}

function Test-PathWithRepair {
  param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$Description
  )

  try {
    return Test-Path -LiteralPath $Path -ErrorAction Stop
  } catch [System.UnauthorizedAccessException] {
    Repair-PathAccess -Path $Path -Description $Description
    try {
      return Test-Path -LiteralPath $Path -ErrorAction Stop
    } catch {
      throw "$Description exists but permissions could not be repaired"
    }
  }
}

function Set-RequiredAcl {
  param([Parameter(Mandatory = $true)][string]$Path)

  $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
  if ($null -eq $acl) {
    throw "Get-Acl returned no ACL for $Path"
  }
  $acl.SetAccessRuleProtection($true, $false)
  $rules = $acl.GetAccessRules($true, $true, [System.Security.Principal.NTAccount])
  foreach ($rule in $rules) {
    $acl.RemoveAccessRule($rule) | Out-Null
  }

  $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
  $systemSid = New-Object System.Security.Principal.SecurityIdentifier "S-1-5-18"
  $adminsSid = New-Object System.Security.Principal.SecurityIdentifier "S-1-5-32-544"

  $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($currentUser, "FullControl", "Allow")))
  $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($systemSid, "FullControl", "Allow")))
  $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($adminsSid, "FullControl", "Allow")))

  Set-Acl -LiteralPath $Path -AclObject $acl -ErrorAction Stop
}

$sshDir = Join-Path $env:USERPROFILE ".ssh"
if (!(Test-PathWithRepair -Path $sshDir -Description ".ssh directory")) {
  New-Item -ItemType Directory -Path $sshDir -Force | Out-Null
}
Set-RequiredAcl -Path $sshDir

$authKeys = Join-Path $sshDir "authorized_keys"
if (!(Test-PathWithRepair -Path $authKeys -Description "authorized_keys")) {
  New-Item -ItemType File -Path $authKeys -Force | Out-Null
}
if (!(Test-PathWithRepair -Path $authKeys -Description "authorized_keys")) {
  throw "authorized_keys exists but permissions could not be repaired"
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
  Set-RequiredAcl -Path $authKeys
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
