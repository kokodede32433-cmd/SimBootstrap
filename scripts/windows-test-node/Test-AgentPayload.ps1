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

$text = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes($dll))
if (-not $text.Contains("SimulatorStatusCommandHandler")) {
    throw "Published payload does not contain simulator status handler marker."
}

Write-Host "SimAgent payload verification passed."
