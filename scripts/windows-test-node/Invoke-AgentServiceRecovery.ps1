[CmdletBinding()]
param(
    [int] $BackupIndex = 0
)

$ErrorActionPreference = "Stop"

$serviceName = "SimAgentService"
$agentDirectory = "C:\Program Files\SimBootstrap\Agent"
$backupRoot = "C:\Program Files\SimBootstrap\Agent.backups"

Get-Process -Name "SimAgent.SessionHost" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue

$svc = Get-Service -Name $serviceName -ErrorAction Stop
if ($svc.Status -ne "Stopped") {
    Stop-Service -Name $serviceName -Force
    (Get-Service -Name $serviceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
}

$backups = @(
    Get-ChildItem -Path $backupRoot -Directory -ErrorAction Stop |
        Sort-Object LastWriteTime -Descending
)

if ($backups.Count -eq 0) {
    throw "No Agent backup directory is available."
}
if ($BackupIndex -lt 0 -or $BackupIndex -ge $backups.Count) {
    throw "Requested Agent backup index is not available."
}

$backup = $backups[$BackupIndex]

if (Test-Path $agentDirectory) {
    Remove-Item -Path $agentDirectory -Recurse -Force
}

Copy-Item -Path $backup.FullName -Destination $agentDirectory -Recurse -Force
Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(30))

Write-Host "SimAgentService restored from selected local backup and is Running."
