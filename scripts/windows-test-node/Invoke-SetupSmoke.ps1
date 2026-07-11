[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ReleaseTag,

    [Parameter(Mandatory = $true)]
    [string] $SetupAssetName,

    [Parameter(Mandatory = $true)]
    [ValidateSet("setup-smoke")]
    [string] $TestMode,

    [Parameter(Mandatory = $true)]
    [string] $RunId,

    [string] $Repository = "kokodede32433-cmd/SimBootstrap",

    [string] $RootDirectory = "C:\SimPlatformTestNode"
)

$ErrorActionPreference = "Stop"

$serviceName = "SimBootstrapAgent"
$programFilesRoot = "C:\Program Files\SimBootstrap"
$programDataRoot = "C:\ProgramData\SimBootstrap"
$agentExePath = Join-Path $programFilesRoot "Agent\SimBootstrap.Agent.exe"
$agentSettingsPath = Join-Path $programDataRoot "config\agentsettings.json"
$installResultPath = Join-Path $programDataRoot "state\installation-result.json"
$logsPath = Join-Path $programDataRoot "logs"
$runDirectory = Join-Path (Join-Path $RootDirectory "runs") $RunId
$downloadDirectory = Join-Path $runDirectory "download"
$artifactDirectory = Join-Path $runDirectory "artifacts"
$setupPath = Join-Path $downloadDirectory $SetupAssetName
$validationReportPath = Join-Path $artifactDirectory "validation-report.json"

function New-Directory {
    param([string] $Path)
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-ServiceSnapshot {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        return [ordered]@{
            Exists = $false
            Status = "Missing"
            ServiceName = $serviceName
        }
    }

    $wmiService = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    return [ordered]@{
        Exists = $true
        Status = $service.Status.ToString()
        ServiceName = $serviceName
        ProcessId = if ($wmiService) { $wmiService.ProcessId } else { $null }
        StartMode = if ($wmiService) { $wmiService.StartMode } else { $null }
        State = if ($wmiService) { $wmiService.State } else { $null }
        PathName = if ($wmiService) { $wmiService.PathName } else { $null }
    }
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
    $logFile = Join-Path $artifactDirectory "cleanup-log.txt"
    $logBuilder = [System.Text.StringBuilder]::new()
    $logBuilder.AppendLine("Cleanup started at $((Get-Date).ToString('O'))") | Out-Null

    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        $logBuilder.AppendLine("Service '$serviceName' is not present; continuing cleanup.") | Out-Null
    } else {
        $logBuilder.AppendLine("Service '$serviceName' exists. Current status: $($service.Status)") | Out-Null
        if ($service.Status -ne "Stopped") {
            $logBuilder.AppendLine("Attempting to stop service '$serviceName'...") | Out-Null
            try {
                Stop-Service -Name $serviceName -Force -ErrorAction Stop
                $logBuilder.AppendLine("Stop command sent. Waiting for status 'Stopped'...") | Out-Null
                $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
                $logBuilder.AppendLine("Service successfully stopped.") | Out-Null
            } catch {
                $err = $_.Exception.Message
                $statusSnapshot = (Get-Service -Name $serviceName).Status
                $logBuilder.AppendLine("ERROR: Failed to stop service. Status: $statusSnapshot. Error: $err") | Out-Null
                $logBuilder.ToString() | Set-Content -Path $logFile -Encoding UTF8
                throw "Failed to stop service '$serviceName'. Status: $statusSnapshot. Error: $err"
            }
        } else {
            $logBuilder.AppendLine("Service '$serviceName' is already stopped.") | Out-Null
        }

        $logBuilder.AppendLine("Deleting service '$serviceName'...") | Out-Null
        try {
            $deleteOutput = & sc.exe delete $serviceName 2>&1
            $logBuilder.AppendLine("sc.exe delete output: $deleteOutput") | Out-Null
            Start-Sleep -Seconds 2
        } catch {
            $logBuilder.AppendLine("WARNING: sc.exe delete failed: $($_.Exception.Message)") | Out-Null
        }
    }

    foreach ($path in @($programFilesRoot, $programDataRoot)) {
        if (Test-Path $path) {
            if ($path -notin @("C:\Program Files\SimBootstrap", "C:\ProgramData\SimBootstrap")) {
                $logBuilder.AppendLine("ERROR: Refusing to remove unexpected path: $path") | Out-Null
                $logBuilder.ToString() | Set-Content -Path $logFile -Encoding UTF8
                throw "Refusing to remove unexpected path: $path"
            }
            $logBuilder.AppendLine("Removing directory: $path") | Out-Null
            try {
                Remove-Item -Path $path -Recurse -Force -ErrorAction Stop
                $logBuilder.AppendLine("Directory $path successfully removed.") | Out-Null
            } catch {
                $logBuilder.AppendLine("WARNING: Failed to remove $path: $($_.Exception.Message)") | Out-Null
            }
        }
    }

    $logBuilder.AppendLine("Cleanup completed successfully.") | Out-Null
    $logBuilder.ToString() | Set-Content -Path $logFile -Encoding UTF8
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
    $processes = @(Get-Process -Name "SimBootstrap.Agent" -ErrorAction SilentlyContinue | ForEach-Object {
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
| Artifact | `$ArtifactName` |

"@ | Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding UTF8
}

New-Directory $downloadDirectory
New-Directory $artifactDirectory

$preState = [ordered]@{
    Timestamp = (Get-Date).ToString("O")
    Service = Get-ServiceSnapshot
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

$windowsVersion = Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber, OSArchitecture
Write-JsonFile $windowsVersion (Join-Path $artifactDirectory "windows-version.json")
$PSVersionTable | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $artifactDirectory "powershell-version.json") -Encoding UTF8
(& dotnet --info 2>&1) | Set-Content -Path (Join-Path $artifactDirectory "dotnet-info.txt") -Encoding UTF8

$setupStdout = Join-Path $runDirectory "setup-stdout.raw.txt"
$setupStderr = Join-Path $runDirectory "setup-stderr.raw.txt"
$process = Start-Process -FilePath $setupPath -ArgumentList @("--pair-code", "LOCAL-PAIR-CODE") -Wait -PassThru -NoNewWindow -RedirectStandardOutput $setupStdout -RedirectStandardError $setupStderr

$resultAppeared = Wait-ForInstallationResult -Timeout ([TimeSpan]::FromSeconds(120))
if (-not $resultAppeared) {
    throw "installation-result.json did not appear within 120 seconds after Setup execution."
}

$installationResult = Get-Content -Raw -Path $installResultPath | ConvertFrom-Json
$service = Get-ServiceSnapshot
$agentProcess = Get-AgentProcessInfo
$agentLogs = @(Get-ChildItem -Path $logsPath -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
$recentAgentLogs = @($agentLogs | Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-15) })
$fatalLogHits = @()
foreach ($log in $recentAgentLogs) {
    $fatalLogHits += @(Select-String -Path $log.FullName -Pattern "Fatal|Unhandled exception|StackTrace|Access denied" -SimpleMatch:$false -ErrorAction SilentlyContinue | Select-Object -First 20)
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
    Files = [ordered]@{
        AgentExeExists = Test-Path $agentExePath
        AgentSettingsExists = Test-Path $agentSettingsPath
        LogsDirectoryExists = Test-Path $logsPath
    }
    Agent = [ordered]@{
        Result = "Unknown"
        Process = $agentProcess
        RecentLogCount = $recentAgentLogs.Count
        FatalStartupHits = @($fatalLogHits | ForEach-Object { ConvertTo-RedactedText ("{0}:{1}: {2}" -f $_.Path, $_.LineNumber, $_.Line) })
    }
}

$failures = New-Object System.Collections.Generic.List[string]
if ($process.ExitCode -ne 0) { $failures.Add("Setup exited with code $($process.ExitCode).") }
if (-not $validation.Installation.Success) { $failures.Add("installation-result.json Success was not true.") }
if (-not $validation.Service.Exists) { $failures.Add("SimBootstrapAgent service does not exist.") }
if ($validation.Service.Status -ne "Running") { $failures.Add("SimBootstrapAgent service status is $($validation.Service.Status), expected Running.") }
if (-not $validation.Files.AgentExeExists) { $failures.Add("$agentExePath is missing.") }
if (-not $validation.Files.AgentSettingsExists) { $failures.Add("$agentSettingsPath is missing.") }
if (-not $validation.Files.LogsDirectoryExists) { $failures.Add("$logsPath is missing.") }
if (($null -eq $agentProcess.ServiceProcessId) -or ($agentProcess.ServiceProcessId -le 0)) { $failures.Add("Service PID could not be resolved.") }
if ($recentAgentLogs.Count -eq 0) { $failures.Add("No recent Agent logs found.") }
if ($fatalLogHits.Count -gt 0) { $failures.Add("Recent Agent logs contain fatal startup errors.") }

if ($failures.Count -eq 0) {
    $validation.Agent.Result = "Passed"
} else {
    $validation.Success = $false
    $validation.Agent.Result = "Failed"
    $validation.Failures = @($failures)
}

Write-JsonFile $validation $validationReportPath

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
Get-Process -Name "SimBootstrap.Agent" -ErrorAction SilentlyContinue |
    Select-Object Id, ProcessName, Path, StartTime |
    ConvertTo-Json -Depth 4 |
    Set-Content -Path (Join-Path $artifactDirectory "agent-processes.json") -Encoding UTF8

Add-StepSummary $validation "windows-test-node-setup-smoke-$RunId"

if (-not $validation.Success) {
    throw "Windows Test Node setup smoke validation failed: $($failures -join '; ')"
}

Write-Host "Windows Test Node setup smoke validation passed."
