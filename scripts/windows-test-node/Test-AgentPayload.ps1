[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PayloadDirectory
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $PayloadDirectory "SimAgent.Service.exe"
if (-not (Test-Path $exe)) {
    throw "SimAgent.Service.exe missing from publish output."
}

$dll = Join-Path $PayloadDirectory "SimAgent.Service.dll"
if (-not (Test-Path $dll)) {
    throw "SimAgent.Service.dll missing from publish output."
}

$sessionHostExe = Join-Path $PayloadDirectory "SimAgent.SessionHost.exe"
if (-not (Test-Path $sessionHostExe)) {
    throw "SimAgent.SessionHost.exe missing from publish output."
}

$sessionHostDll = Join-Path $PayloadDirectory "SimAgent.SessionHost.dll"
if (-not (Test-Path $sessionHostDll)) {
    throw "SimAgent.SessionHost.dll missing from publish output."
}

$text = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($dll))
if (-not $text.Contains("SimulatorStatusCommandHandler")) {
    throw "Published payload does not contain simulator status handler marker."
}

$sessionText = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($sessionHostDll))
if (-not $sessionText.Contains("SessionHostWorker")) {
    throw "Published payload does not contain SessionHost marker."
}

Write-Host "SimAgent payload verification passed."
