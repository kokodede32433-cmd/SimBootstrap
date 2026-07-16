[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $SimAgentDirectory,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory
)

$ErrorActionPreference = "Stop"

$packageDirectory = Join-Path $OutputDirectory "package"
$agentOutput = Join-Path $packageDirectory "Agent"
$sessionHostOutput = Join-Path $packageDirectory "SessionHost"

New-Item -ItemType Directory -Force -Path $agentOutput | Out-Null
New-Item -ItemType Directory -Force -Path $sessionHostOutput | Out-Null

Push-Location $SimAgentDirectory
try {
    $sha = (git rev-parse HEAD).Trim()
} finally {
    Pop-Location
}

$version = "1.0.0+$sha"

dotnet publish (Join-Path $SimAgentDirectory "src/SimAgent.Service/SimAgent.Service.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -o $agentOutput `
    /p:Version=1.0.0 `
    /p:InformationalVersion=$version

dotnet publish (Join-Path $SimAgentDirectory "src/SimAgent.SessionHost/SimAgent.SessionHost.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -o $sessionHostOutput `
    /p:Version=1.0.0 `
    /p:InformationalVersion=$version

[ordered]@{
    sourceCommit = $sha
    version = $version
    packageKind = "SimAgent.Service+SessionHost"
    createdUtc = (Get-Date).ToUniversalTime().ToString("o")
    includes = @("Agent", "SessionHost")
} | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $packageDirectory "version-manifest.json") -Encoding UTF8

Write-Host "SimAgent candidate package created."
