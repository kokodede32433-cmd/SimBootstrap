[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $RunId,

    [string] $ExpectedSimAgentCommit = "",

    [string] $RootDirectory = "C:\SimPlatformTestNode"
)

$ErrorActionPreference = "Stop"

$programFilesRoot = "C:\Program Files\SimBootstrap"
$programDataRoot = "C:\ProgramData\SimBootstrap"
$sessionHostDirectory = Join-Path $programFilesRoot "SessionHost"
$sessionHostExePath = Join-Path $sessionHostDirectory "SimAgent.SessionHost.exe"
$serviceExePath = Join-Path (Join-Path $programFilesRoot "Agent") "SimAgent.Service.exe"
$pipeName = "SimAgentSessionHostCommands"
$runDirectory = Join-Path (Join-Path $RootDirectory "runs") $RunId
$artifactDirectory = Join-Path $runDirectory "artifacts"
$reportPath = Join-Path $artifactDirectory "sessionhost-runtime-diagnostics.json"

$markers = @(
    "SESSION_HOST_PROCESS_STARTED",
    "SESSION_HOST_USER_RESOLVED",
    "SESSION_HOST_SESSION_RESOLVED",
    "COMMAND_PIPE_ACL_BUILD_START",
    "COMMAND_PIPE_ACL_BUILD_SUCCESS",
    "COMMAND_PIPE_CREATE_START",
    "COMMAND_PIPE_CREATE_SUCCESS",
    "COMMAND_PIPE_WAITING",
    "COMMAND_PIPE_FATAL_ERROR",
    "SESSION_HOST_WORKER_EXIT",
    "COMMAND_PIPE_STARTING",
    "COMMAND_PIPE_READY",
    "COMMAND_CLIENT_CONNECTED",
    "COMMAND_RECEIVED",
    "COMMAND_COMPLETED",
    "COMMAND_PIPE_STOPPED"
)

function New-Directory {
    param([string] $Path)
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)] $Value,
        [Parameter(Mandatory = $true)] [string] $Path
    )
    $Value | ConvertTo-Json -Depth 12 | Set-Content -Path $Path -Encoding UTF8
}

function Get-SafeFileVersion {
    param([string] $Path)

    if (-not (Test-Path $Path)) {
        return [ordered]@{
            Exists = $false
            FileVersion = $null
            ProductVersion = $null
        }
    }

    try {
        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
        return [ordered]@{
            Exists = $true
            FileVersion = $info.FileVersion
            ProductVersion = $info.ProductVersion
        }
    } catch {
        return [ordered]@{
            Exists = $true
            FileVersion = $null
            ProductVersion = $null
        }
    }
}

function ConvertTo-SafeErrorCode {
    param([string] $Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    if ($Text -match "FileNotFoundException|FileLoadException|Could not load file|assembly") { return "SESSION_HOST_DEPENDENCY_MISSING" }
    if ($Text -match "UnauthorizedAccessException|Access is denied|PipeSecurity|AccessControl") { return "PIPE_ACL_BUILD_FAILED" }
    if ($Text -match "NamedPipeServerStream|CreateSecurePipe|NamedPipeServerStreamAcl") { return "PIPE_CREATE_FAILED" }
    if ($Text -match "SessionHostWorker|Host terminated|terminated unexpectedly") { return "SESSION_HOST_STARTUP_FAILED" }
    return "SESSION_HOST_STARTUP_FAILED"
}

function Test-CommandPipe {
    param([string] $Context)

    $result = [ordered]@{
        Context = $Context
        CanConnect = $false
        ResponseReceived = $false
        Success = $false
        SafeErrorCode = $null
    }

    try {
        $client = [System.IO.Pipes.NamedPipeClientStream]::new(
            ".",
            $pipeName,
            [System.IO.Pipes.PipeDirection]::InOut,
            [System.IO.Pipes.PipeOptions]::WriteThrough,
            [System.Security.Principal.TokenImpersonationLevel]::Identification)
        try {
            $client.Connect(3000)
            $result.CanConnect = $true
            $client.ReadMode = [System.IO.Pipes.PipeTransmissionMode]::Message
            $client.ReadTimeout = 3000
            $request = [ordered]@{
                RequestId = [guid]::NewGuid()
                Method = "GET_SESSION_STATUS"
                ParametersJson = (@{
                    protocolVersion = "1.0"
                    applicationId = $null
                } | ConvertTo-Json -Compress)
                TimestampUtc = (Get-Date).ToUniversalTime().ToString("o")
            } | ConvertTo-Json -Compress

            $bytes = [System.Text.Encoding]::UTF8.GetBytes($request)
            $client.Write($bytes, 0, $bytes.Length)
            $client.Flush()

            $buffer = New-Object byte[] 65536
            $ms = New-Object System.IO.MemoryStream
            do {
                $read = $client.Read($buffer, 0, $buffer.Length)
                if ($read -le 0) { break }
                $ms.Write($buffer, 0, $read)
            } while (-not $client.IsMessageComplete)

            $responseJson = [System.Text.Encoding]::UTF8.GetString($ms.ToArray())
            if (-not [string]::IsNullOrWhiteSpace($responseJson)) {
                $result.ResponseReceived = $true
                $response = $responseJson | ConvertFrom-Json
                $result.Success = $response.Success -eq $true
                if (-not $result.Success) {
                    $result.SafeErrorCode = if ($response.ErrorMessage) { [string]$response.ErrorMessage } else { "PIPE_RESPONSE_FAILED" }
                }
            } else {
                $result.SafeErrorCode = "PIPE_SERVER_NOT_LISTENING"
            }
        } finally {
            $client.Dispose()
        }
    } catch [System.TimeoutException] {
        $result.SafeErrorCode = "SESSION_HOST_TIMEOUT"
    } catch [System.UnauthorizedAccessException] {
        $result.SafeErrorCode = "PIPE_ACL_ACCESS_DENIED"
    } catch {
        $result.SafeErrorCode = "SESSION_HOST_UNAVAILABLE"
    }

    return $result
}

function Invoke-ScheduledPipeProbe {
    param(
        [Parameter(Mandatory = $true)] [string] $TaskName,
        [Parameter(Mandatory = $true)] [ValidateSet("System", "Interactive")] [string] $Mode
    )

    $probeScript = Join-Path $artifactDirectory "$TaskName.ps1"
    $probeOutput = Join-Path $artifactDirectory "$TaskName.json"
    $probeCode = @"
`$ErrorActionPreference = "Stop"
function Test-CommandPipe {
    param([string] `$Context)
    `$pipeName = "$pipeName"
    `$result = [ordered]@{ Context = `$Context; CanConnect = `$false; ResponseReceived = `$false; Success = `$false; SafeErrorCode = `$null }
    try {
        `$client = [System.IO.Pipes.NamedPipeClientStream]::new(".", `$pipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::WriteThrough, [System.Security.Principal.TokenImpersonationLevel]::Identification)
        try {
            `$client.Connect(3000)
            `$result.CanConnect = `$true
            `$client.ReadMode = [System.IO.Pipes.PipeTransmissionMode]::Message
            `$client.ReadTimeout = 3000
            `$request = [ordered]@{ RequestId = [guid]::NewGuid(); Method = "GET_SESSION_STATUS"; ParametersJson = (@{ protocolVersion = "1.0"; applicationId = `$null } | ConvertTo-Json -Compress); TimestampUtc = (Get-Date).ToUniversalTime().ToString("o") } | ConvertTo-Json -Compress
            `$bytes = [System.Text.Encoding]::UTF8.GetBytes(`$request)
            `$client.Write(`$bytes, 0, `$bytes.Length)
            `$client.Flush()
            `$buffer = New-Object byte[] 65536
            `$ms = New-Object System.IO.MemoryStream
            do {
                `$read = `$client.Read(`$buffer, 0, `$buffer.Length)
                if (`$read -le 0) { break }
                `$ms.Write(`$buffer, 0, `$read)
            } while (-not `$client.IsMessageComplete)
            `$responseJson = [System.Text.Encoding]::UTF8.GetString(`$ms.ToArray())
            if (-not [string]::IsNullOrWhiteSpace(`$responseJson)) {
                `$result.ResponseReceived = `$true
                `$response = `$responseJson | ConvertFrom-Json
                `$result.Success = `$response.Success -eq `$true
                if (-not `$result.Success) { `$result.SafeErrorCode = if (`$response.ErrorMessage) { [string]`$response.ErrorMessage } else { "PIPE_RESPONSE_FAILED" } }
            } else {
                `$result.SafeErrorCode = "PIPE_SERVER_NOT_LISTENING"
            }
        } finally {
            `$client.Dispose()
        }
    } catch [System.TimeoutException] {
        `$result.SafeErrorCode = "SESSION_HOST_TIMEOUT"
    } catch [System.UnauthorizedAccessException] {
        `$result.SafeErrorCode = "PIPE_ACL_ACCESS_DENIED"
    } catch {
        `$result.SafeErrorCode = "SESSION_HOST_UNAVAILABLE"
    }
    return `$result
}
Test-CommandPipe "$Mode" | ConvertTo-Json -Depth 8 | Set-Content -Path "$probeOutput" -Encoding UTF8
"@
    $probeCode | Set-Content -Path $probeScript -Encoding UTF8

    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    try {
        $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$probeScript`""
        $trigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(1))
        if ($Mode -eq "System") {
            $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
        } else {
            $interactive = Get-InteractiveUserContext
            if ($null -eq $interactive) {
                return [ordered]@{ Context = $Mode; CanConnect = $false; ResponseReceived = $false; Success = $false; SafeErrorCode = "INTERACTIVE_USER_NOT_RESOLVED" }
            }
            $principal = New-ScheduledTaskPrincipal -UserId $interactive.UserName -LogonType Interactive -RunLevel Limited
        }
        $task = New-ScheduledTask -Action $action -Trigger $trigger -Principal $principal
        Register-ScheduledTask -TaskName $TaskName -InputObject $task -Force | Out-Null
        Start-ScheduledTask -TaskName $TaskName

        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline) {
            if (Test-Path $probeOutput) {
                return Get-Content -Path $probeOutput -Raw | ConvertFrom-Json
            }
            Start-Sleep -Milliseconds 500
        }

        return [ordered]@{ Context = $Mode; CanConnect = $false; ResponseReceived = $false; Success = $false; SafeErrorCode = "PIPE_PROBE_TIMEOUT" }
    } catch {
        return [ordered]@{ Context = $Mode; CanConnect = $false; ResponseReceived = $false; Success = $false; SafeErrorCode = "PIPE_PROBE_FAILED" }
    } finally {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    }
}

function Get-InteractiveUserContext {
    $explorerProcesses = @(
        Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.SessionId -gt 0 }
    )

    foreach ($process in $explorerProcesses) {
        $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwner -ErrorAction SilentlyContinue
        if ($null -eq $owner -or [string]::IsNullOrWhiteSpace($owner.User)) {
            continue
        }

        $qualifiedUser = if ([string]::IsNullOrWhiteSpace($owner.Domain)) {
            $owner.User
        } else {
            "$($owner.Domain)\$($owner.User)"
        }

        return [ordered]@{
            UserName = $qualifiedUser
            SessionId = $process.SessionId
        }
    }

    return $null
}

function Get-StartupMarkers {
    $logRoots = @(
        (Join-Path $sessionHostDirectory "logs"),
        (Join-Path $programDataRoot "logs")
    )
    $found = @()
    foreach ($root in $logRoots) {
        if (-not (Test-Path $root)) { continue }
        $logs = Get-ChildItem -Path $root -File -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -gt (Get-Date).AddHours(-12) } |
            Sort-Object LastWriteTime
        foreach ($log in $logs) {
            $lines = Get-Content -Path $log.FullName -ErrorAction SilentlyContinue
            foreach ($line in $lines) {
                foreach ($marker in $markers) {
                    if ($line -like "*$marker*") {
                        $safeError = $null
                        if ($line -match "PIPE_[A-Z_]+|SESSION_HOST_[A-Z_]+") {
                            $safeError = $matches[0]
                        }
                        $found += [ordered]@{
                            Marker = $marker
                            SafeErrorCode = $safeError
                        }
                    }
                }
            }
        }
    }
    return @($found)
}

function Get-SafeEventLogSummary {
    $events = @()
    try {
        $events = Get-WinEvent -FilterHashtable @{
            LogName = "Application"
            StartTime = (Get-Date).AddHours(-12)
        } -ErrorAction SilentlyContinue |
            Where-Object {
                ($_.ProviderName -in @(".NET Runtime", "Application Error", "Windows Error Reporting")) -and
                ($_.Message -like "*SimAgent.SessionHost*")
            } |
            Select-Object -First 12
    } catch {
        return @([ordered]@{ EventLogReadable = $false; SafeErrorCode = "EVENT_LOG_READ_FAILED" })
    }

    return @($events | ForEach-Object {
        [ordered]@{
            EventLogReadable = $true
            ProviderName = $_.ProviderName
            EventId = $_.Id
            Level = $_.LevelDisplayName
            SafeErrorCode = ConvertTo-SafeErrorCode $_.Message
        }
    })
}

New-Directory $artifactDirectory

$sessionHostProcesses = @(Get-Process -Name "SimAgent.SessionHost" -ErrorAction SilentlyContinue)
$processInfo = @($sessionHostProcesses | ForEach-Object {
    $startTimeUtc = $null
    $hasExited = $false
    try { $startTimeUtc = $_.StartTime.ToUniversalTime().ToString("o") } catch { $startTimeUtc = $null }
    try { $hasExited = $_.HasExited } catch { $hasExited = $false }

    [ordered]@{
        SessionId = $_.SessionId
        InteractiveSession = $_.SessionId -gt 0
        StartTimeUtc = $startTimeUtc
        HasExited = $hasExited
    }
})

$runnerPipe = Test-CommandPipe "Runner"
$systemPipe = Invoke-ScheduledPipeProbe -TaskName "SimAgentPipeDiagSystem" -Mode "System"
$interactivePipe = Invoke-ScheduledPipeProbe -TaskName "SimAgentPipeDiagInteractive" -Mode "Interactive"
$markerSequence = Get-StartupMarkers

$pipeCreationStage = if (@($markerSequence | Where-Object { $_.Marker -eq "COMMAND_PIPE_CREATE_SUCCESS" -or $_.Marker -eq "COMMAND_PIPE_READY" }).Count -gt 0) {
    "COMMAND_PIPE_CREATE_SUCCESS"
} elseif (@($markerSequence | Where-Object { $_.Marker -eq "COMMAND_PIPE_CREATE_START" -or $_.Marker -eq "COMMAND_PIPE_STARTING" }).Count -gt 0) {
    "COMMAND_PIPE_CREATE_START"
} elseif (@($markerSequence | Where-Object { $_.Marker -eq "COMMAND_PIPE_ACL_BUILD_SUCCESS" }).Count -gt 0) {
    "COMMAND_PIPE_ACL_BUILD_SUCCESS"
} elseif (@($markerSequence | Where-Object { $_.Marker -eq "COMMAND_PIPE_ACL_BUILD_START" }).Count -gt 0) {
    "COMMAND_PIPE_ACL_BUILD_START"
} else {
    "NO_STARTUP_MARKERS"
}

$classification = if ($sessionHostProcesses.Count -eq 0) {
    "SESSION_HOST_NOT_RUNNING"
} elseif ($pipeCreationStage -eq "NO_STARTUP_MARKERS") {
    "INSTALLED_BUILD_HAS_NO_STARTUP_MARKERS"
} elseif (($runnerPipe.Success -or $systemPipe.Success -or $interactivePipe.Success) -and $pipeCreationStage -eq "COMMAND_PIPE_CREATE_SUCCESS") {
    "PIPE_HEALTHY"
} elseif ($runnerPipe.Success -and -not $systemPipe.Success) {
    "PIPE_ACL_SYSTEM_DENIED"
} elseif (-not $runnerPipe.Success -and -not $systemPipe.Success -and $pipeCreationStage -eq "COMMAND_PIPE_CREATE_SUCCESS") {
    "PIPE_SERVER_NOT_LISTENING"
} else {
    "SESSION_HOST_PIPE_UNAVAILABLE"
}

$expectedCommitValue = $null
if (-not [string]::IsNullOrWhiteSpace($ExpectedSimAgentCommit)) {
    $expectedCommitValue = $ExpectedSimAgentCommit
}

$report = [ordered]@{
    GeneratedAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    ExpectedSimAgentCommit = $expectedCommitValue
    InstalledCommitVerifiable = $false
    InstalledSessionHostVersion = Get-SafeFileVersion $sessionHostExePath
    InstalledServiceVersion = Get-SafeFileVersion $serviceExePath
    SessionHostProcessCount = $sessionHostProcesses.Count
    SessionHostProcesses = $processInfo
    StartupMarkerSequence = @($markerSequence)
    PipeCreationStage = $pipeCreationStage
    RunnerConnectivity = $runnerPipe
    LocalSystemConnectivity = $systemPipe
    InteractiveConnectivity = $interactivePipe
    EventLogSafeSummary = @(Get-SafeEventLogSummary)
    ExactFailureClassification = $classification
}

Write-JsonFile $report $reportPath
Write-JsonFile $report (Join-Path $artifactDirectory "qa-result.json")

Write-Host "SessionHost runtime diagnostics complete."
