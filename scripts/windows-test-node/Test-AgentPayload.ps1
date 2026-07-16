[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PayloadDirectory,

    [string] $ExpectedCommit = ""
)

$ErrorActionPreference = "Stop"

$agentPayloadDirectory = Join-Path $PayloadDirectory "Agent"
$sessionHostPayloadDirectory = Join-Path $PayloadDirectory "SessionHost"
if (-not (Test-Path $agentPayloadDirectory)) {
    $agentPayloadDirectory = $PayloadDirectory
}
if (-not (Test-Path $sessionHostPayloadDirectory)) {
    $sessionHostPayloadDirectory = $PayloadDirectory
}

$exe = Join-Path $agentPayloadDirectory "SimAgent.Service.exe"
if (-not (Test-Path $exe)) {
    throw "SimAgent.Service.exe missing from publish output."
}

$dll = Join-Path $agentPayloadDirectory "SimAgent.Service.dll"
if (-not (Test-Path $dll)) {
    throw "SimAgent.Service.dll missing from publish output."
}

$sessionHostExe = Join-Path $sessionHostPayloadDirectory "SimAgent.SessionHost.exe"
if (-not (Test-Path $sessionHostExe)) {
    throw "SimAgent.SessionHost.exe missing from publish output."
}

$sessionHostDll = Join-Path $sessionHostPayloadDirectory "SimAgent.SessionHost.dll"
if (-not (Test-Path $sessionHostDll)) {
    throw "SimAgent.SessionHost.dll missing from publish output."
}

$manifestPath = Join-Path $PayloadDirectory "version-manifest.json"
if (-not (Test-Path $manifestPath)) {
    throw "version-manifest.json missing from publish output."
}

function Get-PayloadCommit {
    param([string] $Path)

    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if ($info.ProductVersion -match "\+([0-9a-fA-F]{40})") {
        return $matches[1].ToLowerInvariant()
    }
    return $null
}

$manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
$manifestCommit = if ($manifest.sourceCommit) { [string]$manifest.sourceCommit } else { "" }
$manifestCommit = $manifestCommit.ToLowerInvariant()
$serviceCommit = Get-PayloadCommit $exe
$sessionHostCommit = Get-PayloadCommit $sessionHostExe

if ([string]::IsNullOrWhiteSpace($manifestCommit)) {
    throw "Payload manifest source commit is missing."
}
if ([string]::IsNullOrWhiteSpace($serviceCommit)) {
    throw "SimAgent.Service commit marker is missing."
}
if ([string]::IsNullOrWhiteSpace($sessionHostCommit)) {
    throw "SimAgent.SessionHost commit marker is missing."
}
if ($serviceCommit -ne $sessionHostCommit -or $serviceCommit -ne $manifestCommit) {
    throw "Payload commit markers do not match."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit) -and $manifestCommit -ne $ExpectedCommit.ToLowerInvariant()) {
    throw "Payload commit does not match expected commit."
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
