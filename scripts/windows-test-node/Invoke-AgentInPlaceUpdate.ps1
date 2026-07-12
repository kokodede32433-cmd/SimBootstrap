[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PayloadDirectory,

    [Parameter(Mandatory = $true)]
    [string] $RunId,

    [Parameter(Mandatory = $true)]
    [string] $InstalledSimAgentCommit,

    [string] $RootDirectory = "C:\SimPlatformTestNode"
)

$ErrorActionPreference = "Stop"

$serviceName = "SimAgentService"
$legacyServiceName = "SimBootstrapAgent"
$programFilesRoot = "C:\Program Files\SimBootstrap"
$programDataRoot = "C:\ProgramData\SimBootstrap"
$agentDirectory = Join-Path $programFilesRoot "Agent"
$sessionHostDirectory = Join-Path $programFilesRoot "SessionHost"
$agentExePath = Join-Path $agentDirectory "SimAgent.Service.exe"
$sessionHostExePath = Join-Path $sessionHostDirectory "SimAgent.SessionHost.exe"
$sessionHostShortcutName = "SimAgent.SessionHost.lnk"
$agentSettingsPath = Join-Path $programDataRoot "config\agentsettings.json"
$approvedAppsPath = Join-Path $programDataRoot "config\approved-apps.json"
$backupRoot = Join-Path $programFilesRoot "Agent.backups"
$sessionHostBackupRoot = Join-Path $programFilesRoot "SessionHost.backups"
$runDirectory = Join-Path (Join-Path $RootDirectory "runs") $RunId
$artifactDirectory = Join-Path $runDirectory "artifacts"
$validationReportPath = Join-Path $artifactDirectory "qa-result.json"

function New-Directory {
    param([string] $Path)
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)] $Value,
        [Parameter(Mandatory = $true)] [string] $Path
    )
    $Value | ConvertTo-Json -Depth 10 | Set-Content -Path $Path -Encoding UTF8
}

function ConvertTo-RedactedText {
    param([string] $Text)
    $redacted = $Text
    $patterns = @(
        '(?i)(Authorization:\s*Bearer\s+)[^\s"]+',
        '(?i)("?(machineCredential|credential|token|accessToken|refreshToken|authorization|password|secret|privateKey|pairCode)"?\s*[:=]\s*"?)[^",\r\n]+',
        '-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----'
    )
    foreach ($pattern in $patterns) {
        $redacted = [regex]::Replace($redacted, $pattern, {
            param($match)
            if ($match.Groups.Count -gt 1 -and $match.Groups[1].Success) {
                return $match.Groups[1].Value + "REDACTED"
            }
            return "REDACTED"
        })
    }
    return $redacted
}

function Assert-RequiredSecret {
    param(
        [Parameter(Mandatory = $true)] [string] $Name,
        [string] $Value
    )
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "Missing required secret/environment value: $Name"
    }
}

function Get-ServiceSnapshot {
    param([string] $Name = $serviceName)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        return [ordered]@{
            Exists = $false
            Status = "Missing"
            ServiceName = $Name
        }
    }

    $wmiService = Get-CimInstance Win32_Service -Filter "Name='$Name'" -ErrorAction SilentlyContinue
    return [ordered]@{
        Exists = $true
        Status = $service.Status.ToString()
        ServiceName = $Name
        ProcessId = if ($wmiService) { $wmiService.ProcessId } else { $null }
        StartMode = if ($wmiService) { $wmiService.StartMode } else { $null }
        State = if ($wmiService) { $wmiService.State } else { $null }
        PathName = if ($wmiService) { $wmiService.PathName } else { $null }
    }
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-E2ECommandIssuer {
    param([hashtable] $Body)

    Assert-RequiredSecret "SIMCRM_STAGING_SUPABASE_URL" $env:SIMCRM_STAGING_SUPABASE_URL
    Assert-RequiredSecret "SIMCRM_STAGING_SUPABASE_ANON_KEY" $env:SIMCRM_STAGING_SUPABASE_ANON_KEY
    Assert-RequiredSecret "SIMCRM_STAGING_PAIR_CODE_TOKEN" $env:SIMCRM_STAGING_PAIR_CODE_TOKEN

    $supabaseUrl = $env:SIMCRM_STAGING_SUPABASE_URL.TrimEnd("/")
    $issuerUrl = "$supabaseUrl/functions/v1/create-e2e-agent-command"
    $headers = @{
        apikey = $env:SIMCRM_STAGING_SUPABASE_ANON_KEY
        Authorization = "Bearer $($env:SIMCRM_STAGING_PAIR_CODE_TOKEN)"
        "Content-Type" = "application/json"
    }
    return Invoke-RestMethod -Method Post -Uri $issuerUrl -Headers $headers -Body ($Body | ConvertTo-Json -Compress)
}

function Wait-ForAgentOnline {
    param([TimeSpan] $Timeout)

    $deadline = (Get-Date).Add($Timeout)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        $last = Invoke-E2ECommandIssuer @{ action = "preflight" }
        if ($last.agent.targetAgentConfigured -and $last.agent.isOnline) {
            return $last
        }
        Start-Sleep -Seconds 5
    }
    return $last
}

function Restore-Backup {
    param([string] $BackupPath)

    if ([string]::IsNullOrWhiteSpace($BackupPath) -or -not (Test-Path $BackupPath)) {
        return $false
    }

    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }

    if (Test-Path $agentDirectory) {
        Remove-Item -Path $agentDirectory -Recurse -Force
    }
    Copy-Item -Path $BackupPath -Destination $agentDirectory -Recurse -Force
    Start-Service -Name $serviceName
    return $true
}

New-Directory $artifactDirectory
$backupPath = Join-Path $backupRoot ("Agent-" + (Get-Date -Format "yyyyMMddHHmmss"))
$sessionHostBackupPath = Join-Path $sessionHostBackupRoot ("SessionHost-" + (Get-Date -Format "yyyyMMddHHmmss"))
$rollbackAttempted = $false
$rollbackSucceeded = $false
$approvedAppsExistedBefore = Test-Path $approvedAppsPath

$validation = [ordered]@{
    Success = $false
    InstalledSimAgentCommit = $InstalledSimAgentCommit
    BackupPath = $backupPath
    SessionHostBackupPath = $sessionHostBackupPath
    PreflightBefore = $null
    PreflightAfter = $null
    ServiceBefore = $null
    ServiceAfter = $null
    LegacyServiceAfter = $null
    AgentSettingsPreserved = $false
    ApprovedAppsPreserved = $false
    SessionHostExecutableExists = $false
    SessionHostStartupConfigured = $false
    SessionHostRunning = $false
    SessionHostInteractiveSession = $false
    AgentCountUnchanged = $false
    ServicePathPreserved = $false
    ServiceStartModePreserved = $false
    PayloadExecutableExists = $false
    Rollback = [ordered]@{
        Attempted = $false
        Succeeded = $false
    }
    Failures = @()
}

try {
    if (-not (Test-IsAdministrator)) {
        throw "Windows Test Node runner must run elevated for in-place update."
    }
    if (-not (Test-Path $agentSettingsPath)) {
        throw "agentsettings.json is missing; refusing to update because pairing identity must be preserved."
    }
    $agentPayloadDirectory = Join-Path $PayloadDirectory "Agent"
    $sessionHostPayloadDirectory = Join-Path $PayloadDirectory "SessionHost"
    if (-not (Test-Path $agentPayloadDirectory)) {
        $agentPayloadDirectory = $PayloadDirectory
    }
    if (-not (Test-Path $sessionHostPayloadDirectory)) {
        $sessionHostPayloadDirectory = $PayloadDirectory
    }

    if (-not (Test-Path (Join-Path $agentPayloadDirectory "SimAgent.Service.exe"))) {
        throw "Published payload is missing SimAgent.Service.exe."
    }
    if (-not (Test-Path (Join-Path $sessionHostPayloadDirectory "SimAgent.SessionHost.exe"))) {
        throw "Published payload is missing SimAgent.SessionHost.exe."
    }
    $validation.PayloadExecutableExists = $true

    $preflightBefore = Wait-ForAgentOnline ([TimeSpan]::FromSeconds(90))
    $validation.PreflightBefore = $preflightBefore
    if (-not $preflightBefore -or -not $preflightBefore.agent.targetAgentConfigured) {
        throw "Configured E2E target Agent was not found before update."
    }

    $serviceBefore = Get-ServiceSnapshot
    $validation.ServiceBefore = $serviceBefore
    if (-not $serviceBefore.Exists) {
        throw "SimAgentService is missing."
    }
    if ($serviceBefore.PathName -notlike "*SimAgent.Service.exe*" -or $serviceBefore.PathName -notlike "*--SimAgent:AgentSettingsPath*") {
        throw "SimAgentService does not use the canonical SimAgent.Service executable and config argument."
    }
    if (-not (Test-Path $agentDirectory)) {
        throw "Agent binary directory is missing."
    }

    New-Directory $backupRoot
    Copy-Item -Path $agentDirectory -Destination $backupPath -Recurse -Force
    if (Test-Path $sessionHostDirectory) {
        New-Directory $sessionHostBackupRoot
        Copy-Item -Path $sessionHostDirectory -Destination $sessionHostBackupPath -Recurse -Force
    }

    Get-Process -Name "SimAgent.SessionHost" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    if ($serviceBefore.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
        (Get-Service -Name $serviceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }

    Get-ChildItem -Path $agentDirectory -Force | Remove-Item -Recurse -Force
    Copy-Item -Path (Join-Path $agentPayloadDirectory "*") -Destination $agentDirectory -Recurse -Force
    if (Test-Path $sessionHostDirectory) {
        Get-ChildItem -Path $sessionHostDirectory -Force | Remove-Item -Recurse -Force
    } else {
        New-Directory $sessionHostDirectory
    }
    Copy-Item -Path (Join-Path $sessionHostPayloadDirectory "*") -Destination $sessionHostDirectory -Recurse -Force

    if (-not (Test-Path $agentSettingsPath)) {
        throw "agentsettings.json disappeared during payload update."
    }
    $validation.AgentSettingsPreserved = $true
    $validation.ApprovedAppsPreserved = (-not $approvedAppsExistedBefore) -or (Test-Path $approvedAppsPath)
    if (-not (Test-Path $sessionHostExePath)) {
        throw "SimAgent.SessionHost.exe is missing after payload update."
    }
    $validation.SessionHostExecutableExists = $true

    $startupPath = [Environment]::GetFolderPath("Startup")
    if ([string]::IsNullOrWhiteSpace($startupPath)) {
        throw "Unable to resolve interactive user Startup folder."
    }
    $shortcutPath = Join-Path $startupPath $sessionHostShortcutName
    $wsh = New-Object -ComObject WScript.Shell
    $shortcut = $wsh.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $sessionHostExePath
    $shortcut.WorkingDirectory = $agentDirectory
    $shortcut.Save()
    $validation.SessionHostStartupConfigured = Test-Path $shortcutPath

    Start-Process -FilePath $sessionHostExePath -WorkingDirectory $agentDirectory -WindowStyle Hidden
    Start-Sleep -Seconds 5

    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
    Start-Sleep -Seconds 5

    $serviceAfter = Get-ServiceSnapshot
    $legacyAfter = Get-ServiceSnapshot $legacyServiceName
    $preflightAfter = Wait-ForAgentOnline ([TimeSpan]::FromSeconds(120))

    $validation.ServiceAfter = $serviceAfter
    $validation.LegacyServiceAfter = $legacyAfter
    $validation.PreflightAfter = $preflightAfter
    $validation.AgentCountUnchanged = $null -ne $preflightAfter -and [int]$preflightAfter.agentCount -eq [int]$preflightBefore.agentCount
    $validation.ServicePathPreserved = $serviceAfter.PathName -eq $serviceBefore.PathName
    $validation.ServiceStartModePreserved = $serviceAfter.StartMode -eq $serviceBefore.StartMode
    $sessionHostProcesses = @(Get-Process -Name "SimAgent.SessionHost" -ErrorAction SilentlyContinue)
    $validation.SessionHostRunning = $sessionHostProcesses.Count -gt 0
    $validation.SessionHostInteractiveSession = @($sessionHostProcesses | Where-Object { $_.SessionId -gt 0 }).Count -gt 0

    if ($serviceAfter.Status -ne "Running") {
        throw "SimAgentService is not Running after update."
    }
    if (-not $validation.SessionHostRunning) {
        throw "SimAgent.SessionHost is not running after update."
    }
    if (-not $validation.SessionHostInteractiveSession) {
        throw "SimAgent.SessionHost is not running in an interactive session."
    }
    if ($legacyAfter.Exists) {
        throw "Legacy SimBootstrapAgent service exists after update."
    }
    if ($null -eq $preflightAfter -or -not $preflightAfter.agent.targetAgentConfigured -or -not $preflightAfter.agent.isOnline) {
        throw "Agent heartbeat did not become online/recent after update."
    }
    if (-not $validation.AgentCountUnchanged) {
        throw "Agent count changed during update."
    }
    if (-not $validation.ServicePathPreserved -or -not $validation.ServiceStartModePreserved) {
        throw "Service registration changed during update."
    }

    $validation.Success = $true
} catch {
    $validation.Failures = @([string]$_.Exception.Message)
    try {
        $rollbackAttempted = $true
        $rollbackSucceeded = Restore-Backup $backupPath
        $validation.ServiceAfter = Get-ServiceSnapshot
        $validation.PreflightAfter = Wait-ForAgentOnline ([TimeSpan]::FromSeconds(90))
    } catch {
        $validation.Failures += "Rollback failed: $($_.Exception.Message)"
    }
    $validation.Rollback.Attempted = $rollbackAttempted
    $validation.Rollback.Succeeded = $rollbackSucceeded
}

Write-JsonFile $validation $validationReportPath
Write-JsonFile $validation (Join-Path $artifactDirectory "validation-report.json")

try {
    $recentLogs = @(
        Get-ChildItem -Path (Join-Path $programDataRoot "logs") -File -ErrorAction SilentlyContinue
        Get-ChildItem -Path (Join-Path $agentDirectory "logs") -File -ErrorAction SilentlyContinue
    ) | Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-15) } | Select-Object -First 10
    foreach ($log in $recentLogs) {
        $destination = Join-Path $artifactDirectory ("logs\" + $log.Name + ".redacted.txt")
        New-Directory (Split-Path -Parent $destination)
        ConvertTo-RedactedText (Get-Content -Raw -Path $log.FullName) | Set-Content -Path $destination -Encoding UTF8
    }
} catch {}

if (-not $validation.Success) {
    throw "In-place SimAgent update failed: $($validation.Failures -join '; ')"
}

Write-Host "In-place SimAgent update passed."
