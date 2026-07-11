[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ReleaseTag,

    [Parameter(Mandatory = $true)]
    [string] $SetupAssetName,

    [Parameter(Mandatory = $true)]
    [ValidateSet("real-pairing-e2e")]
    [string] $TestMode,

    [Parameter(Mandatory = $true)]
    [string] $RunId,

    [string] $Repository = "kokodede32433-cmd/SimBootstrap",

    [string] $RootDirectory = "C:\SimPlatformTestNode"
)

$ErrorActionPreference = "Stop"

$serviceName = "SimAgentService"
$legacyServiceName = "SimBootstrapAgent"
$programFilesRoot = "C:\Program Files\SimBootstrap"
$programDataRoot = "C:\ProgramData\SimBootstrap"
$agentExePath = Join-Path $programFilesRoot "Agent\SimAgent.Service.exe"
$agentSettingsPath = Join-Path $programDataRoot "config\agentsettings.json"
$installResultPath = Join-Path $programDataRoot "state\installation-result.json"
$logsPath = Join-Path $programDataRoot "logs"
$serviceLogsPath = Join-Path $programFilesRoot "Agent\logs"
$runDirectory = Join-Path (Join-Path $RootDirectory "runs") $RunId
$downloadDirectory = Join-Path $runDirectory "download"
$artifactDirectory = Join-Path $runDirectory "artifacts"
$setupPath = Join-Path $downloadDirectory $SetupAssetName
$validationReportPath = Join-Path $artifactDirectory "qa-result.json"

function New-Directory {
    param([string] $Path)
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
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

function Assert-RequiredSecret {
    param(
        [Parameter(Mandatory = $true)] [string] $Name,
        [string] $Value
    )
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "Missing required secret/environment value: $Name"
    }
}

function New-StagingPairCode {
    Assert-RequiredSecret "SIMCRM_STAGING_SUPABASE_URL" $env:SIMCRM_STAGING_SUPABASE_URL
    Assert-RequiredSecret "SIMCRM_STAGING_PAIR_CODE_TOKEN" $env:SIMCRM_STAGING_PAIR_CODE_TOKEN
    Assert-RequiredSecret "SIMCRM_STAGING_PAIRING_LOCATION_ID" $env:SIMCRM_STAGING_PAIRING_LOCATION_ID

    $supabaseUrl = $env:SIMCRM_STAGING_SUPABASE_URL.TrimEnd("/")
    $rpcUrl = "$supabaseUrl/rest/v1/rpc/create_agent_pairing_code_v1"
    $body = @{ p_location_id = $env:SIMCRM_STAGING_PAIRING_LOCATION_ID } | ConvertTo-Json -Compress
    $apiKey = if ([string]::IsNullOrWhiteSpace($env:SIMCRM_STAGING_SUPABASE_ANON_KEY)) {
        $env:SIMCRM_STAGING_PAIR_CODE_TOKEN
    } else {
        $env:SIMCRM_STAGING_SUPABASE_ANON_KEY
    }
    $headers = @{
        apikey = $apiKey
        Authorization = "Bearer $($env:SIMCRM_STAGING_PAIR_CODE_TOKEN)"
        "Content-Type" = "application/json"
    }

    $response = Invoke-RestMethod -Method Post -Uri $rpcUrl -Headers $headers -Body $body
    $pairCode = if ($response.pairingCode) { $response.pairingCode } elseif ($response.pairingcode) { $response.pairingcode } else { $null }
    if ([string]::IsNullOrWhiteSpace($pairCode)) {
        throw "Pair Code RPC did not return a usable pairing code."
    }

    Write-Host "::add-mask::$pairCode"
    return [string]$pairCode
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)] $Value,
        [Parameter(Mandatory = $true)] [string] $Path
    )
    $Value | ConvertTo-Json -Depth 8 | Set-Content -Path $Path -Encoding UTF8
}

function ConvertTo-RedactedText {
    param([string] $Text)
    $redacted = $Text
    $patterns = @(
        '(?i)(Authorization:\s*Bearer\s+)[^\s"]+',
        '(?i)("?(machineCredential|credential|token|accessToken|refreshToken|authorization|password|secret|privateKey|pairCode)"?\s*[:=]\s*"?)[^",\r\n]+',
        '(?i)(LOCAL-PAIR-CODE)',
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

function Copy-RedactedFile {
    param(
        [Parameter(Mandatory = $true)] [string] $Source,
        [Parameter(Mandatory = $true)] [string] $Destination
    )
    if (-not (Test-Path $Source)) {
        return
    }

    New-Directory (Split-Path -Parent $Destination)
    $text = Get-Content -Raw -Path $Source -ErrorAction Stop
    ConvertTo-RedactedText $text | Set-Content -Path $Destination -Encoding UTF8
}

function Remove-QaState {
    Write-Host "Cleaning known SimBootstrap QA service and directories only."

    foreach ($name in @($serviceName, $legacyServiceName)) {
        $service = Get-Service -Name $name -ErrorAction SilentlyContinue
        if ($service) {
            if ($service.Status -ne "Stopped") {
                Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
                $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
            }

            & sc.exe delete $name | Out-File -FilePath (Join-Path $artifactDirectory "cleanup-service-delete-$name.txt") -Encoding UTF8
            Start-Sleep -Seconds 2
        }
    }

    foreach ($path in @($programFilesRoot, $programDataRoot)) {
        if (Test-Path $path) {
            if ($path -notin @("C:\Program Files\SimBootstrap", "C:\ProgramData\SimBootstrap")) {
                throw "Refusing to remove unexpected path: $path"
            }
            Remove-Item -Path $path -Recurse -Force -ErrorAction Stop
        }
    }
}

function Wait-ForInstallationResult {
    param([TimeSpan] $Timeout)
    $deadline = (Get-Date).Add($Timeout)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $installResultPath) {
            return $true
        }
        Start-Sleep -Seconds 1
    }
    return $false
}

function Get-AgentProcessInfo {
    $serviceInfo = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    $processes = @(Get-Process -Name "SimAgent.Service" -ErrorAction SilentlyContinue | ForEach-Object {
        [ordered]@{
            Id = $_.Id
            ProcessName = $_.ProcessName
            Path = $_.Path
            StartTime = try { $_.StartTime.ToString("O") } catch { $null }
        }
    })

    return [ordered]@{
        ServiceProcessId = if ($serviceInfo) { $serviceInfo.ProcessId } else { $null }
        Processes = $processes
        LegacyProcesses = @(Get-Process -Name "SimBootstrap.Agent" -ErrorAction SilentlyContinue | ForEach-Object {
            [ordered]@{
                Id = $_.Id
                ProcessName = $_.ProcessName
                Path = $_.Path
                StartTime = try { $_.StartTime.ToString("O") } catch { $null }
            }
        })
    }
}

function Add-StepSummary {
    param(
        [Parameter(Mandatory = $true)] $Validation,
        [Parameter(Mandatory = $true)] [string] $ArtifactName
    )
    if (-not $env:GITHUB_STEP_SUMMARY) {
        return
    }

    @"
## Windows Test Node Setup Smoke

| Field | Value |
|---|---|
| Release | `$ReleaseTag` |
| Setup asset | `$SetupAssetName` |
| Test mode | `$TestMode` |
| Installation result | `$($Validation.Installation.Success)` |
| Service result | `$($Validation.Service.Status)` |
| Agent result | `$($Validation.Agent.Result)` |
| Canonical executable | `$($Validation.Service.CanonicalExecutable)` |
| Artifact | `$ArtifactName` |

"@ | Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding UTF8
}

New-Directory $downloadDirectory
New-Directory $artifactDirectory

$preState = [ordered]@{
    Timestamp = (Get-Date).ToString("O")
    Service = Get-ServiceSnapshot
    LegacyService = Get-ServiceSnapshot $legacyServiceName
    ProgramFilesExists = Test-Path $programFilesRoot
    ProgramDataExists = Test-Path $programDataRoot
    AgentProcesses = Get-AgentProcessInfo
}
Write-JsonFile $preState (Join-Path $artifactDirectory "pre-test-state.json")

if (-not (Test-IsAdministrator)) {
    $message = "Windows Test Node runner must run elevated for Setup smoke validation. Configure the self-hosted runner service with administrative rights or run the runner elevated."
    $failure = [ordered]@{
        Success = $false
        Error = $message
        ReleaseTag = $ReleaseTag
        SetupAssetName = $SetupAssetName
    }
    Write-JsonFile $failure $validationReportPath
    Add-StepSummary @{ Installation = @{ Success = $false }; Service = @{ Status = "NotChecked" }; Agent = @{ Result = "Blocked" } } "windows-test-node-setup-smoke-$RunId"
    throw $message
}

Remove-QaState

$downloadUrl = "https://github.com/$Repository/releases/download/$ReleaseTag/$SetupAssetName"
Write-Host "Downloading $downloadUrl"
Invoke-WebRequest -Uri $downloadUrl -OutFile $setupPath -UseBasicParsing

$controlServerUrl = if ([string]::IsNullOrWhiteSpace($env:SIMBOOTSTRAP_STAGING_CONTROL_SERVER_URL)) {
    "https://api-staging.hi-racing.ru/functions/v1/pair-agent"
} else {
    $env:SIMBOOTSTRAP_STAGING_CONTROL_SERVER_URL
}
@{
    controlServerUrl = $controlServerUrl
} | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $downloadDirectory "setupconfig.json") -Encoding UTF8

$windowsVersion = Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, OSArchitecture
Write-JsonFile $windowsVersion (Join-Path $artifactDirectory "windows-version.json")
$PSVersionTable | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $artifactDirectory "powershell-version.json") -Encoding UTF8
(& dotnet --info 2>&1) | Set-Content -Path (Join-Path $artifactDirectory "dotnet-info.txt") -Encoding UTF8

$setupStdout = Join-Path $runDirectory "setup-stdout.raw.txt"
$setupStderr = Join-Path $runDirectory "setup-stderr.raw.txt"
$pairCode = New-StagingPairCode
$process = Start-Process -FilePath $setupPath -ArgumentList @("--pair-code", $pairCode) -Wait -PassThru -NoNewWindow -RedirectStandardOutput $setupStdout -RedirectStandardError $setupStderr
$pairCode = $null

$resultAppeared = Wait-ForInstallationResult -Timeout ([TimeSpan]::FromSeconds(120))
if (-not $resultAppeared) {
    throw "installation-result.json did not appear within 120 seconds after Setup execution."
}

$installationResult = Get-Content -Raw -Path $installResultPath | ConvertFrom-Json
$service = Get-ServiceSnapshot
$legacyService = Get-ServiceSnapshot $legacyServiceName
$agentProcess = Get-AgentProcessInfo
$agentLogs = @(
    Get-ChildItem -Path $logsPath -File -ErrorAction SilentlyContinue
    Get-ChildItem -Path $serviceLogsPath -File -ErrorAction SilentlyContinue
) | Sort-Object LastWriteTime -Descending
$recentAgentLogs = @($agentLogs | Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-15) })
$fatalLogHits = @()
$configLoadedHits = @()
foreach ($log in $recentAgentLogs) {
    $fatalLogHits += @(Select-String -Path $log.FullName -Pattern "Fatal|Unhandled exception|StackTrace|Access denied" -SimpleMatch:$false -ErrorAction SilentlyContinue | Select-Object -First 20)
    $configLoadedHits += @(Select-String -Path $log.FullName -Pattern "Starting SimAgent Service Host" -SimpleMatch -ErrorAction SilentlyContinue | Select-Object -First 5)
}

$secretLogHits = @()
foreach ($candidate in @($setupStdout, $setupStderr, $installResultPath) + @($recentAgentLogs | ForEach-Object { $_.FullName })) {
    if (Test-Path $candidate) {
        $secretLogHits += @(Select-String -Path $candidate -Pattern '(?i)machineCredential|Authorization:\s*Bearer|pairCode"\s*:' -ErrorAction SilentlyContinue | Select-Object -First 20)
    }
}

$validation = [ordered]@{
    Success = $true
    ReleaseTag = $ReleaseTag
    SetupAssetName = $SetupAssetName
    TestMode = $TestMode
    SetupExitCode = $process.ExitCode
    Installation = [ordered]@{
        ResultFileExists = Test-Path $installResultPath
        Success = [bool]$installationResult.Success
        State = $installationResult.State
        Message = $installationResult.Message
    }
    Service = $service
    LegacyService = $legacyService
    Files = [ordered]@{
        AgentExeExists = Test-Path $agentExePath
        AgentSettingsExists = Test-Path $agentSettingsPath
        LogsDirectoryExists = Test-Path $logsPath
        ServiceLogsDirectoryExists = Test-Path $serviceLogsPath
    }
    Agent = [ordered]@{
        Result = "Unknown"
        Process = $agentProcess
        RecentLogCount = $recentAgentLogs.Count
        FatalStartupHits = @($fatalLogHits | ForEach-Object { ConvertTo-RedactedText "$($_.Path):$($_.LineNumber): $($_.Line)" })
        ConfigLoaded = $configLoadedHits.Count -gt 0
        SecretPatternHits = @($secretLogHits | ForEach-Object { ConvertTo-RedactedText "$($_.Path):$($_.LineNumber): $($_.Line)" })
    }
}
$validation.Service.CanonicalExecutable = $false
if ($validation.Service.PathName) {
    $validation.Service.CanonicalExecutable = ($validation.Service.PathName -like "*SimAgent.Service.exe*" -and $validation.Service.PathName -like "*--SimAgent:AgentSettingsPath*")
}

$failures = New-Object System.Collections.Generic.List[string]
if ($process.ExitCode -ne 0) { $failures.Add("Setup exited with code $($process.ExitCode).") }
if (-not $validation.Installation.Success) { $failures.Add("installation-result.json Success was not true.") }
if (-not $validation.Service.Exists) { $failures.Add("SimAgentService service does not exist.") }
if ($validation.Service.Status -ne "Running") { $failures.Add("SimAgentService service status is $($validation.Service.Status), expected Running.") }
if (-not $validation.Service.CanonicalExecutable) { $failures.Add("SimAgentService is not targeting SimAgent.Service.exe with the canonical config path argument.") }
if ($validation.LegacyService.Exists) { $failures.Add("Legacy SimBootstrapAgent service exists; it must not run as a production Agent.") }
if (-not $validation.Files.AgentExeExists) { $failures.Add("$agentExePath is missing.") }
if (-not $validation.Files.AgentSettingsExists) { $failures.Add("$agentSettingsPath is missing.") }
if (-not $validation.Files.LogsDirectoryExists) { $failures.Add("$logsPath is missing.") }
if (($null -eq $agentProcess.ServiceProcessId) -or ($agentProcess.ServiceProcessId -le 0)) { $failures.Add("Service PID could not be resolved.") }
if ($recentAgentLogs.Count -eq 0) { $failures.Add("No recent Agent logs found.") }
if (-not $validation.Agent.ConfigLoaded) { $failures.Add("Recent logs do not confirm SimAgent.Service startup after real config load.") }
if ($agentProcess.LegacyProcesses.Count -gt 0) { $failures.Add("Legacy SimBootstrap.Agent process is running.") }
if ($fatalLogHits.Count -gt 0) { $failures.Add("Recent Agent logs contain fatal startup errors.") }
if ($secretLogHits.Count -gt 0) { $failures.Add("Logs or artifacts contain secret-shaped fields.") }

if ($failures.Count -eq 0) {
    $validation.Agent.Result = "Passed"
} else {
    $validation.Success = $false
    $validation.Agent.Result = "Failed"
    $validation.Failures = @($failures)
}

Write-JsonFile $validation $validationReportPath
Write-JsonFile $validation (Join-Path $artifactDirectory "validation-report.json")

Copy-RedactedFile $installResultPath (Join-Path $artifactDirectory "installation-result.redacted.json")
Copy-RedactedFile $setupStdout (Join-Path $artifactDirectory "setup-stdout.redacted.txt")
Copy-RedactedFile $setupStderr (Join-Path $artifactDirectory "setup-stderr.redacted.txt")
if (Test-Path (Join-Path $logsPath "simbootstrap-setup.log")) {
    Copy-RedactedFile (Join-Path $logsPath "simbootstrap-setup.log") (Join-Path $artifactDirectory "logs\simbootstrap-setup.redacted.log")
}
foreach ($log in $agentLogs | Select-Object -First 10) {
    Copy-RedactedFile $log.FullName (Join-Path $artifactDirectory ("logs\" + $log.Name + ".redacted.txt"))
}

(& sc.exe queryex $serviceName 2>&1) | Set-Content -Path (Join-Path $artifactDirectory "service-queryex.txt") -Encoding UTF8
(& sc.exe queryex $legacyServiceName 2>&1) | Set-Content -Path (Join-Path $artifactDirectory "legacy-service-queryex.txt") -Encoding UTF8
Get-Process -Name "SimAgent.Service" -ErrorAction SilentlyContinue |
    Select-Object Id, ProcessName, Path, StartTime |
    ConvertTo-Json -Depth 4 |
    Set-Content -Path (Join-Path $artifactDirectory "agent-processes.json") -Encoding UTF8
Get-Process -Name "SimBootstrap.Agent" -ErrorAction SilentlyContinue |
    Select-Object Id, ProcessName, Path, StartTime |
    ConvertTo-Json -Depth 4 |
    Set-Content -Path (Join-Path $artifactDirectory "legacy-agent-processes.json") -Encoding UTF8

Add-StepSummary $validation "windows-test-node-setup-smoke-$RunId"

if (-not $validation.Success) {
    throw "Windows Test Node setup smoke validation failed: $($failures -join '; ')"
}

Write-Host "Windows Test Node setup smoke validation passed."
