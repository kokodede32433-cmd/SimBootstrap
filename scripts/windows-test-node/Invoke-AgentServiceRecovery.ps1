[CmdletBinding()]
param(
    [int] $BackupIndex = -1,

    [Parameter(Mandatory = $true)]
    [string] $RunId,

    [string] $RootDirectory = "C:\SimPlatformTestNode"
)

$ErrorActionPreference = "Stop"

$serviceName = "SimAgentService"
$programFilesRoot = "C:\Program Files\SimBootstrap"
$programDataRoot = "C:\ProgramData\SimBootstrap"
$agentDirectory = Join-Path $programFilesRoot "Agent"
$sessionHostDirectory = Join-Path $programFilesRoot "SessionHost"
$backupRoot = Join-Path $programFilesRoot "Agent.backups"
$sessionHostBackupRoot = Join-Path $programFilesRoot "SessionHost.backups"
$agentSettingsPath = Join-Path $programDataRoot "config\agentsettings.json"
$sessionHostShortcutName = "SimAgent.SessionHost.lnk"
$pipeName = "SimAgentSessionHostCommands"
$runDirectory = Join-Path (Join-Path $RootDirectory "runs") $RunId
$artifactDirectory = Join-Path $runDirectory "artifacts"
$reportPath = Join-Path $artifactDirectory "qa-result.json"

function New-Directory {
    param([string] $Path)
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)] $Value,
        [Parameter(Mandatory = $true)] [string] $Path
    )
    $Value | ConvertTo-Json -Depth 14 | Set-Content -Path $Path -Encoding UTF8
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

function Get-SafeVersionInfo {
    param([string] $Path)

    if (-not (Test-Path $Path)) {
        return [ordered]@{
            Exists = $false
            Commit = $null
            ProductVersion = $null
        }
    }

    try {
        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
        $commit = $null
        if ($info.ProductVersion -match "\+([0-9a-fA-F]{40})") {
            $commit = $matches[1].ToLowerInvariant()
        }
        return [ordered]@{
            Exists = $true
            Commit = $commit
            ProductVersion = $info.ProductVersion
        }
    } catch {
        return [ordered]@{
            Exists = $true
            Commit = $null
            ProductVersion = $null
        }
    }
}

function Test-SharedAssemblies {
    param([string] $Directory)

    $required = @(
        "SimAgent.Abstractions.dll",
        "SimAgent.Contracts.dll",
        "SimAgent.Protocol.dll",
        "SimAgent.SDK.dll",
        "SimAgent.Shared.dll",
        "SimAgent.Windows.dll"
    )

    foreach ($name in $required) {
        if (-not (Test-Path (Join-Path $Directory $name))) {
            return $false
        }
    }
    return $true
}

function Get-BackupTimestamp {
    param([string] $Name)

    if ($Name -match "(\d{14})$") {
        return $matches[1]
    }
    return $null
}

function Get-BackupAudit {
    $agentBackups = @{}
    if (Test-Path $backupRoot) {
        Get-ChildItem -Path $backupRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $ts = Get-BackupTimestamp $_.Name
            if ($ts) { $agentBackups[$ts] = $_ }
        }
    }

    $sessionBackups = @{}
    if (Test-Path $sessionHostBackupRoot) {
        Get-ChildItem -Path $sessionHostBackupRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $ts = Get-BackupTimestamp $_.Name
            if ($ts) { $sessionBackups[$ts] = $_ }
        }
    }

    $timestamps = @($agentBackups.Keys + $sessionBackups.Keys | Sort-Object -Descending -Unique)
    $items = @()
    $index = 0
    foreach ($ts in $timestamps) {
        $agent = if ($agentBackups.ContainsKey($ts)) { $agentBackups[$ts] } else { $null }
        $session = if ($sessionBackups.ContainsKey($ts)) { $sessionBackups[$ts] } else { $null }
        $serviceExe = if ($agent) { Join-Path $agent.FullName "SimAgent.Service.exe" } else { "" }
        $sessionExe = if ($session) { Join-Path $session.FullName "SimAgent.SessionHost.exe" } else { "" }
        $serviceVersion = Get-SafeVersionInfo $serviceExe
        $sessionVersion = Get-SafeVersionInfo $sessionExe
        $servicePresent = $serviceVersion.Exists
        $sessionPresent = $sessionVersion.Exists
        $sharedPresent = ($agent -and (Test-SharedAssemblies $agent.FullName)) -and ($session -and (Test-SharedAssemblies $session.FullName))
        $consistent = $servicePresent -and $sessionPresent -and $sharedPresent -and
            -not [string]::IsNullOrWhiteSpace($serviceVersion.Commit) -and
            $serviceVersion.Commit -eq $sessionVersion.Commit

        $items += [ordered]@{
            Index = $index
            CreationTimestamp = $ts
            ServicePresent = [bool]$servicePresent
            SessionHostPresent = [bool]$sessionPresent
            SharedAssembliesPresent = [bool]$sharedPresent
            ServiceCommit = $serviceVersion.Commit
            SessionHostCommit = $sessionVersion.Commit
            InternallyConsistent = [bool]$consistent
            AgentBackupObject = $agent
            SessionHostBackupObject = $session
        }
        $index++
    }
    return @($items)
}

function Get-PublicBackupSummary {
    param([array] $Items)

    return @($Items | ForEach-Object {
        $commitMarker = if ($_.ServiceCommit -and $_.ServiceCommit -eq $_.SessionHostCommit) {
            $_.ServiceCommit
        } else {
            "MIXED_OR_UNKNOWN"
        }
        [ordered]@{
            BackupIndex = $_.Index
            CreationTimestamp = $_.CreationTimestamp
            ServicePresent = $_.ServicePresent
            SessionHostPresent = $_.SessionHostPresent
            SharedAssembliesPresent = $_.SharedAssembliesPresent
            SourceCommitVersionMarker = $commitMarker
            InternallyConsistent = $_.InternallyConsistent
        }
    })
}

function Stop-AgentProcesses {
    Get-Process -Name "SimAgent.SessionHost" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    $svc = Get-Service -Name $serviceName -ErrorAction Stop
    if ($svc.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
        (Get-Service -Name $serviceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
}

function Get-InteractiveUserContext {
    $explorerProcesses = @(
        Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.SessionId -gt 0 }
    )

    foreach ($process in $explorerProcesses) {
        $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwner -ErrorAction SilentlyContinue
        $ownerSid = Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid -ErrorAction SilentlyContinue
        if ($null -eq $owner -or [string]::IsNullOrWhiteSpace($owner.User) -or $null -eq $ownerSid -or [string]::IsNullOrWhiteSpace($ownerSid.Sid)) {
            continue
        }

        $profile = Get-CimInstance Win32_UserProfile -Filter "SID='$($ownerSid.Sid)'" -ErrorAction SilentlyContinue
        if ($null -eq $profile -or [string]::IsNullOrWhiteSpace($profile.LocalPath) -or -not (Test-Path $profile.LocalPath)) {
            continue
        }

        $qualifiedUser = if ([string]::IsNullOrWhiteSpace($owner.Domain)) {
            $owner.User
        } else {
            "$($owner.Domain)\$($owner.User)"
        }

        return [ordered]@{
            UserName = $qualifiedUser
            ProfilePath = $profile.LocalPath
            SessionId = $process.SessionId
        }
    }
    return $null
}

function Restore-StartupRegistration {
    $interactive = Get-InteractiveUserContext
    if ($null -eq $interactive) {
        return [ordered]@{ Success = $false; SafeErrorCode = "INTERACTIVE_USER_NOT_RESOLVED" }
    }

    $startupPath = Join-Path $interactive.ProfilePath "AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup"
    New-Directory $startupPath
    $shortcutPath = Join-Path $startupPath $sessionHostShortcutName
    $wsh = New-Object -ComObject WScript.Shell
    $shortcut = $wsh.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $sessionHostDirectory "SimAgent.SessionHost.exe"
    $shortcut.WorkingDirectory = $sessionHostDirectory
    $shortcut.Save()

    return [ordered]@{ Success = (Test-Path $shortcutPath); SafeErrorCode = $null; SessionId = $interactive.SessionId }
}

function Start-SessionHostForInteractiveUser {
    $interactive = Get-InteractiveUserContext
    if ($null -eq $interactive) {
        return [ordered]@{ Success = $false; SafeErrorCode = "INTERACTIVE_USER_NOT_RESOLVED" }
    }

    $taskName = "SimAgentSessionHostRecoveryStart"
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    try {
        $action = New-ScheduledTaskAction -Execute (Join-Path $sessionHostDirectory "SimAgent.SessionHost.exe") -WorkingDirectory $sessionHostDirectory
        $trigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(1))
        $principal = New-ScheduledTaskPrincipal -UserId $interactive.UserName -LogonType Interactive -RunLevel Limited
        $task = New-ScheduledTask -Action $action -Trigger $trigger -Principal $principal
        Register-ScheduledTask -TaskName $taskName -InputObject $task -Force | Out-Null
        Start-ScheduledTask -TaskName $taskName
        Start-Sleep -Seconds 5
        return [ordered]@{ Success = $true; SafeErrorCode = $null }
    } catch {
        return [ordered]@{ Success = $false; SafeErrorCode = "SESSION_HOST_START_FAILED" }
    } finally {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    }
}

function Test-CommandPipe {
    param([string] $Context)

    $result = [ordered]@{
        Context = $Context
        CanConnect = $false
        ResponseReceived = $false
        Success = $false
        ProtocolAccepted = $false
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
                $result.ProtocolAccepted = $response.ErrorMessage -ne "SESSION_HOST_PROTOCOL_MISMATCH"
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

function Invoke-SystemPipeProbe {
    $taskName = "SimAgentRecoverySystemPipeProbe"
    $probeScript = Join-Path $artifactDirectory "$taskName.ps1"
    $probeOutput = Join-Path $artifactDirectory "$taskName.json"
    $probeCode = @"
`$ErrorActionPreference = "Stop"
`$result = [ordered]@{ Context = "System"; CanConnect = `$false; ResponseReceived = `$false; Success = `$false; ProtocolAccepted = `$false; SafeErrorCode = `$null }
try {
    `$client = [System.IO.Pipes.NamedPipeClientStream]::new(".", "$pipeName", [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::WriteThrough, [System.Security.Principal.TokenImpersonationLevel]::Identification)
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
            `$result.ProtocolAccepted = `$response.ErrorMessage -ne "SESSION_HOST_PROTOCOL_MISMATCH"
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
`$result | ConvertTo-Json -Depth 8 | Set-Content -Path "$probeOutput" -Encoding UTF8
"@
    $probeCode | Set-Content -Path $probeScript -Encoding UTF8
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    try {
        $action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$probeScript`""
        $trigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(1))
        $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
        $task = New-ScheduledTask -Action $action -Trigger $trigger -Principal $principal
        Register-ScheduledTask -TaskName $taskName -InputObject $task -Force | Out-Null
        Start-ScheduledTask -TaskName $taskName
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline) {
            if (Test-Path $probeOutput) {
                return Get-Content -Path $probeOutput -Raw | ConvertFrom-Json
            }
            Start-Sleep -Milliseconds 500
        }
        return [ordered]@{ Context = "System"; CanConnect = $false; ResponseReceived = $false; Success = $false; ProtocolAccepted = $false; SafeErrorCode = "PIPE_PROBE_TIMEOUT" }
    } catch {
        return [ordered]@{ Context = "System"; CanConnect = $false; ResponseReceived = $false; Success = $false; ProtocolAccepted = $false; SafeErrorCode = "PIPE_PROBE_FAILED" }
    } finally {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    }
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

function Invoke-SecureCommandSmoke {
    param([ValidateSet("PING", "GET_SIM_STATUS")] [string] $CommandType)

    $preflight = Invoke-E2ECommandIssuer @{ action = "preflight" }
    $agentCountBefore = [int]$preflight.agentCount
    if (-not $preflight.agent.targetAgentConfigured -or -not $preflight.agent.isOnline) {
        return [ordered]@{ Success = $false; Status = "preflight_failed"; AgentCountUnchanged = $false; AgentIdentityPreserved = $false; AttemptCount = $null }
    }

    $action = if ($CommandType -eq "GET_SIM_STATUS") { "create_get_sim_status" } else { "create" }
    $create = Invoke-E2ECommandIssuer @{ action = $action }
    if ([string]::IsNullOrWhiteSpace($create.commandId)) {
        return [ordered]@{ Success = $false; Status = "create_failed"; AgentCountUnchanged = $false; AgentIdentityPreserved = $false; AttemptCount = $null }
    }

    $deadline = (Get-Date).AddMinutes(4)
    $final = $null
    while ((Get-Date) -lt $deadline) {
        $status = Invoke-E2ECommandIssuer @{ action = "status"; commandId = $create.commandId }
        if ($status.status -in @("succeeded", "failed", "expired", "cancelled")) {
            $final = $status
            break
        }
        Start-Sleep -Seconds 10
    }

    if ($null -eq $final) {
        return [ordered]@{ Success = $false; Status = "timeout"; AgentCountUnchanged = $false; AgentIdentityPreserved = $false; AttemptCount = $null }
    }

    $resultOk = $false
    if ($CommandType -eq "PING") {
        $resultOk = $final.status -eq "succeeded" -and $final.result.message -eq "pong"
    } else {
        $resultOk = $final.status -eq "succeeded" -and $final.result.status -eq "succeeded" -and
            ($final.result.PSObject.Properties.Name -contains "applications")
    }

    return [ordered]@{
        Success = [bool]$resultOk
        Status = $final.status
        AttemptCount = [int]$final.attemptCount
        AttemptCountIsOne = ([int]$final.attemptCount -eq 1)
        AgentIdentityPreserved = [bool]$final.agent.targetAgentConfigured
        AgentCountUnchanged = ([int]$final.agentCount -eq $agentCountBefore)
    }
}

function Get-ServiceRuntimeState {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    $wmi = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    $sessionId = $null
    if ($wmi -and $wmi.ProcessId -gt 0) {
        $proc = Get-Process -Id $wmi.ProcessId -ErrorAction SilentlyContinue
        if ($proc) { $sessionId = $proc.SessionId }
    }

    return [ordered]@{
        Exists = ($null -ne $service)
        Status = if ($service) { $service.Status.ToString() } else { "Missing" }
        SessionId = $sessionId
    }
}

function Get-SessionHostRuntimeState {
    $processes = @(Get-Process -Name "SimAgent.SessionHost" -ErrorAction SilentlyContinue)
    return [ordered]@{
        ProcessCount = $processes.Count
        SessionIds = @($processes | ForEach-Object { $_.SessionId })
        HasInteractiveSession = @($processes | Where-Object { $_.SessionId -ge 1 }).Count -ge 1
    }
}

New-Directory $artifactDirectory

$report = [ordered]@{
    Success = $false
    AvailableBackupSummary = @()
    SelectedBackup = $null
    RestoreResult = "not_started"
    ServiceVersion = $null
    SessionHostVersion = $null
    CommitConsistencyResult = "not_checked"
    ServiceStatus = $null
    SessionHostStatus = $null
    PipeConnectionResult = $null
    HandshakeResult = $null
    SecurePingResult = $null
    GetSimStatusResult = $null
    AgentIdentityPreservation = $null
    RollbackScriptCommit = "8816b28"
    RemainingBlocker = $null
}

try {
    if (-not (Test-Path $agentSettingsPath)) {
        throw "agentsettings.json missing; refusing recovery to preserve identity."
    }

    $audit = Get-BackupAudit
    $report.AvailableBackupSummary = Get-PublicBackupSummary $audit
    if ($audit.Count -eq 0) {
        $report.RemainingBlocker = "NO_BACKUPS_AVAILABLE"
        throw "No backups available."
    }

    $selected = $null
    if ($BackupIndex -ge 0) {
        $selected = $audit | Where-Object { $_.Index -eq $BackupIndex } | Select-Object -First 1
        if ($null -eq $selected -or -not $selected.InternallyConsistent) {
            $report.RemainingBlocker = "REQUESTED_BACKUP_NOT_CONSISTENT"
            throw "Requested backup is not internally consistent."
        }
    } else {
        $selected = $audit | Where-Object { $_.InternallyConsistent } | Select-Object -First 1
    }

    if ($null -eq $selected) {
        $report.RemainingBlocker = "NO_INTERNALLY_CONSISTENT_BACKUP"
        throw "No internally consistent Service + SessionHost backup exists."
    }

    $report.SelectedBackup = [ordered]@{
        BackupIndex = $selected.Index
        CreationTimestamp = $selected.CreationTimestamp
        SourceCommitVersionMarker = $selected.ServiceCommit
    }

    $stageRoot = Join-Path $runDirectory "restore-stage"
    $stageAgent = Join-Path $stageRoot "Agent"
    $stageSessionHost = Join-Path $stageRoot "SessionHost"
    if (Test-Path $stageRoot) { Remove-Item -Path $stageRoot -Recurse -Force }
    New-Directory $stageRoot
    Copy-Item -Path $selected.AgentBackupObject.FullName -Destination $stageAgent -Recurse -Force
    Copy-Item -Path $selected.SessionHostBackupObject.FullName -Destination $stageSessionHost -Recurse -Force

    $stageServiceVersion = Get-SafeVersionInfo (Join-Path $stageAgent "SimAgent.Service.exe")
    $stageSessionVersion = Get-SafeVersionInfo (Join-Path $stageSessionHost "SimAgent.SessionHost.exe")
    if ($stageServiceVersion.Commit -ne $stageSessionVersion.Commit) {
        $report.RemainingBlocker = "STAGED_BACKUP_COMMITS_DIFFER"
        throw "Staged backup commits differ."
    }

    Stop-AgentProcesses

    $oldAgent = Join-Path $runDirectory "previous-Agent"
    $oldSessionHost = Join-Path $runDirectory "previous-SessionHost"
    if (Test-Path $agentDirectory) { Move-Item -Path $agentDirectory -Destination $oldAgent -Force }
    if (Test-Path $sessionHostDirectory) { Move-Item -Path $sessionHostDirectory -Destination $oldSessionHost -Force }
    Move-Item -Path $stageAgent -Destination $agentDirectory -Force
    Move-Item -Path $stageSessionHost -Destination $sessionHostDirectory -Force

    $serviceVersion = Get-SafeVersionInfo (Join-Path $agentDirectory "SimAgent.Service.exe")
    $sessionVersion = Get-SafeVersionInfo (Join-Path $sessionHostDirectory "SimAgent.SessionHost.exe")
    $report.ServiceVersion = [ordered]@{ Commit = $serviceVersion.Commit; ProductVersion = $serviceVersion.ProductVersion }
    $report.SessionHostVersion = [ordered]@{ Commit = $sessionVersion.Commit; ProductVersion = $sessionVersion.ProductVersion }
    if ($serviceVersion.Commit -ne $sessionVersion.Commit) {
        $report.CommitConsistencyResult = "failed"
        $report.RemainingBlocker = "RESTORED_COMMITS_DIFFER"
        throw "Restored Service and SessionHost commits differ."
    }
    $report.CommitConsistencyResult = "passed"

    $startup = Restore-StartupRegistration
    if (-not $startup.Success) {
        $report.RemainingBlocker = $startup.SafeErrorCode
        throw "SessionHost startup registration failed."
    }

    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
    Start-Sleep -Seconds 5
    $startSessionHost = Start-SessionHostForInteractiveUser
    if (-not $startSessionHost.Success) {
        $report.RemainingBlocker = $startSessionHost.SafeErrorCode
        throw "SessionHost start failed."
    }

    Start-Sleep -Seconds 5
    $report.ServiceStatus = Get-ServiceRuntimeState
    $report.SessionHostStatus = Get-SessionHostRuntimeState
    if ($report.ServiceStatus.Status -ne "Running" -or $report.ServiceStatus.SessionId -ne 0) {
        $report.RemainingBlocker = "SERVICE_NOT_RUNNING_IN_SESSION_0"
        throw "Service runtime state invalid."
    }
    if ($report.SessionHostStatus.ProcessCount -ne 1 -or -not $report.SessionHostStatus.HasInteractiveSession) {
        $report.RemainingBlocker = "SESSION_HOST_NOT_SINGLE_INTERACTIVE_PROCESS"
        throw "SessionHost runtime state invalid."
    }

    $runnerPipe = Test-CommandPipe "Runner"
    $systemPipe = Invoke-SystemPipeProbe
    $report.PipeConnectionResult = [ordered]@{
        RunnerCanConnect = $runnerPipe.CanConnect
        LocalSystemCanConnect = $systemPipe.CanConnect
    }
    $report.HandshakeResult = [ordered]@{
        Runner = [ordered]@{
            ResponseReceived = $runnerPipe.ResponseReceived
            ProtocolAccepted = $runnerPipe.ProtocolAccepted
            SafeResponseReturned = $runnerPipe.Success
            SafeErrorCode = $runnerPipe.SafeErrorCode
        }
        LocalSystem = [ordered]@{
            ResponseReceived = $systemPipe.ResponseReceived
            ProtocolAccepted = $systemPipe.ProtocolAccepted
            SafeResponseReturned = $systemPipe.Success
            SafeErrorCode = $systemPipe.SafeErrorCode
        }
    }
    if (-not $runnerPipe.Success) {
        $report.RemainingBlocker = "PIPE_HANDSHAKE_FAILED"
        throw "Runner pipe handshake failed."
    }
    if (-not $systemPipe.Success) {
        $report.RemainingBlocker = "LOCAL_SYSTEM_PIPE_HANDSHAKE_FAILED"
        throw "LocalSystem pipe handshake failed."
    }

    $ping = Invoke-SecureCommandSmoke "PING"
    $report.SecurePingResult = $ping
    $report.AgentIdentityPreservation = [ordered]@{
        AgentIdentityPreserved = $ping.AgentIdentityPreserved
        AgentCountUnchanged = $ping.AgentCountUnchanged
    }
    if (-not $ping.Success -or -not $ping.AttemptCountIsOne -or -not $ping.AgentIdentityPreserved -or -not $ping.AgentCountUnchanged) {
        $report.RemainingBlocker = "SECURE_PING_FAILED"
        throw "Secure PING failed."
    }

    $simStatus = Invoke-SecureCommandSmoke "GET_SIM_STATUS"
    $report.GetSimStatusResult = $simStatus
    if (-not $simStatus.Success -or -not $simStatus.AttemptCountIsOne) {
        $report.RemainingBlocker = "GET_SIM_STATUS_FAILED"
        throw "GET_SIM_STATUS failed."
    }

    $report.RestoreResult = "restored"
    $report.Success = $true
    $report.RemainingBlocker = "none"
} catch {
    if ([string]::IsNullOrWhiteSpace($report.RemainingBlocker)) {
        $report.RemainingBlocker = "RECOVERY_FAILED"
    }
    $report.RestoreResult = if ($report.RestoreResult -eq "not_started") { "failed_before_restore" } else { "failed" }
    Write-Host "Recovery stopped: $($report.RemainingBlocker)"
    Write-JsonFile $report $reportPath
    Write-JsonFile $report (Join-Path $artifactDirectory "validation-report.json")
    exit 1
}

Write-JsonFile $report $reportPath
Write-JsonFile $report (Join-Path $artifactDirectory "validation-report.json")
Write-Host "Consistent Agent recovery completed."
