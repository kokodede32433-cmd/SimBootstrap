[CmdletBinding()]
param(
    [string] $Configuration = "Release",
    [string] $RuntimeIdentifier = "win-x64",
    [string] $SimAgentServiceProject = "",
    [string] $OutputDirectory = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if ([string]::IsNullOrWhiteSpace($SimAgentServiceProject)) {
    $SimAgentServiceProject = Join-Path $repoRoot "..\SimAgent\src\SimAgent.Service\SimAgent.Service.csproj"
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\setup-release"
}

$payloadPublishDirectory = Join-Path $repoRoot "artifacts\simagent-service-$RuntimeIdentifier"
$payloadDirectory = Join-Path $repoRoot "src\SimBootstrap.Setup\Payload"
$payloadZip = Join-Path $payloadDirectory "SimAgent.Service-$RuntimeIdentifier.zip"
$setupProject = Join-Path $repoRoot "src\SimBootstrap.Setup\SimBootstrap.Setup.csproj"

Remove-Item -Path $payloadPublishDirectory -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path $OutputDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $payloadPublishDirectory, $payloadDirectory, $OutputDirectory | Out-Null

dotnet publish $SimAgentServiceProject `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained false `
    -o $payloadPublishDirectory

if (-not (Test-Path (Join-Path $payloadPublishDirectory "SimAgent.Service.exe"))) {
    throw "Published SimAgent.Service payload is missing SimAgent.Service.exe."
}

Remove-Item -Path $payloadZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $payloadPublishDirectory "*") -DestinationPath $payloadZip -Force

dotnet publish $setupProject `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:ValidateExecutableReferencesMatchSelfContained=false `
    -o $OutputDirectory

$setupExe = Join-Path $OutputDirectory "SimBootstrap.Setup.exe"
if (-not (Test-Path $setupExe)) {
    throw "Setup release artifact was not produced."
}

Write-Host "Setup release artifact: $setupExe"
