[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PayloadDirectory,

    [Parameter(Mandatory = $true)]
    [string] $RunId,

    [Parameter(Mandatory = $true)]
    [string] $InstalledSimAgentCommit,

    [string] $RootDirectory = "C:\SimPlatformTestNode"
)

$ErrorActionPreference = "Stop"

$serviceName = "SimAgentService"
$legacyServiceName = "SimBootstrapAgent"
$programFilesRoot = "C:\Program Files\SimBootstrap"
$programDataRoot = "C:\ProgramData\SimBootstrap"
$agentDirectory = Join-Path $programFilesRoot "Agent"
$sessionHostDirectory = Join-Path $programFilesRoot "SessionHost"
$agentExePath = Join-Path $agentDirectory "SimAgent.Service.exe"
$sessionHostExePath = Join-Path $sessionHostDirectory "SimAgent.SessionHost.exe"
$sessionHostShortcutName = "SimAgent.SessionHost.lnk"
$agentSettingsPath = Join-Path $programDataRoot "config\agentsettings.json"
$approvedAppsPath = Join-Path $programDataRoot "config\approved-apps.json"
$backupRoot = Join-Path $programFilesRoot "Agent.backups"
$sessionHostBackupRoot = Join-Path $programFilesRoot "SessionHost.backups"
$manifestFileName = "version-manifest.json"
$runDirectory = Join-Path (Join-Path $RootDirectory "runs") $RunId
$artifactDirectory = Join-Path $runDirectory "artifacts"
$validationReportPath = Join-Path $artifactDirectory "qa-result.json"

function New-Directory {
    param([string] $Path)
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)] $Value,
        [Parameter(Mandatory = $true)] [string] $Path
    )
    $Value | ConvertTo-Json -Depth 10 | Set-Content -Path $Path -Encoding UTF8
}

function ConvertTo-RedactedText {
    param([string] $Text)
    $redacted = $Text
    $patterns = @(
        '(?i)(Authorization:\s*Bearer\s+)[^\s"]+',
        '(?i)("?(machineCredential|credential|token|accessToken|refreshToken|authorization|password|secret|privateKey|pairCode)"?\s*[:=]\s*"?)[^",\r\n]+',
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

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path $Path)) {
        return [ordered]@{ Exists = $false; Commit = $null; ProductVersion = $null }
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
        return [ordered]@{ Exists = $true; Commit = $null; ProductVersion = $null }
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

function Get-PackageManifestCommit {
    param([string] $ManifestPath)

    if (-not (Test-Path $ManifestPath)) {
        return $null
    }
    $manifest = Get-Content -Path $ManifestPath -Raw | ConvertFrom-Json
    if ($manifest.sourceCommit) {
        return ([string]$manifest.sourceCommit).ToLowerInvariant()
    }
    return $null
}

function Write-VersionManifest {
    param(
        [Parameter(Mandatory = $true)] [string] $Directory,
        [Parameter(Mandatory = $true)] [string] $Commit,
        [Parameter(Mandatory = $true)] [string] $ProductVersion
    )

    [ordered]@{
        sourceCommit = $Commit
        version = $ProductVersion
        packageKind = "SimAgent.Service+SessionHost"
        createdUtc = (Get-Date).ToUniversalTime().ToString("o")
        includes = @("Agent","SessionHost")
    } | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $Directory $manifestFileName) -Encoding UTF8
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
        StartMode = if ($wmiService) { $wmiService.StartMode } else { $null }
        State = if ($wmiService) { $wmiService.State } else { $null }
        CanonicalExecutable = if ($wmiService) { ($wmiService.PathName -like "*SimAgent.Service.exe*" -and $wmiService.PathName -like "*--SimAgent:AgentSettingsPath*") } else { $false }
    }
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
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
        }
    }

    return $null
}

function Start-SessionHostForInteractiveUser {
    param(
        [Parameter(Mandatory = $true)] [string] $UserName
    )

    $taskName = "SimAgentSessionHostStart"
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue

    $action = New-ScheduledTaskAction -Execute $sessionHostExePath -WorkingDirectory $sessionHostDirectory
    $trigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddMinutes(1))
    $principal = New-ScheduledTaskPrincipal -UserId $UserName -LogonType Interactive -RunLevel Limited
    $task = New-ScheduledTask -Action $action -Trigger $trigger -Principal $principal
    Register-ScheduledTask -TaskName $taskName -InputObject $task -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName
}

function Test-SessionHostCommandPipe {
    param([string] $PipeName = "SimAgentSessionHostCommands")

    $result = [ordered]@{
        Context = "Runner"
        CanConnect = $false
        ResponseReceived = $false
        Success = $false
        ProtocolAccepted = $false
        SafeErrorCode = $null
    }

    try {
        $client = [System.IO.Pipes.NamedPipeClientStream]::new(
            ".",
            $PipeName,
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
            if ([string]::IsNullOrWhiteSpace($responseJson)) {
                $result.SafeErrorCode = "PIPE_SERVER_NOT_LISTENING"
                return $result
            }
            $response = $responseJson | ConvertFrom-Json
            $result.ResponseReceived = $true
            $result.Success = $response.Success -eq $true
            $result.ProtocolAccepted = $response.ErrorMessage -ne "SESSION_HOST_PROTOCOL_MISMATCH"
            if (-not $result.Success) {
                $result.SafeErrorCode = if ($response.ErrorMessage) { [string]$response.ErrorMessage } else { "PIPE_RESPONSE_FAILED" }
            }
            return $result
        } finally {
            $client.Dispose()
        }
    } catch [System.TimeoutException] {
        $result.SafeErrorCode = "SESSION_HOST_TIMEOUT"
    } catch {
        $result.SafeErrorCode = "SESSION_HOST_UNAVAILABLE"
    }
    return $result
}

function Invoke-SystemPipeProbe {
    $taskName = "SimAgentInPlaceSystemPipeProbe"
    $probeScript = Join-Path $artifactDirectory "$taskName.ps1"
    $probeOutput = Join-Path $artifactDirectory "$taskName.json"
    $probeCode = @"
`$ErrorActionPreference = "Stop"
`$result = [ordered]@{ Context = "System"; CanConnect = `$false; ResponseReceived = `$false; Success = `$false; ProtocolAccepted = `$false; SafeErrorCode = `$null }
try {
    `$client = [System.IO.Pipes.NamedPipeClientStream]::new(".", "SimAgentSessionHostCommands", [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::WriteThrough, [System.Security.Principal.TokenImpersonationLevel]::Identification)
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
        if ([string]::IsNullOrWhiteSpace(`$responseJson)) {
            `$result.SafeErrorCode = "PIPE_SERVER_NOT_LISTENING"
        } else {
            `$result.ResponseReceived = `$true
            `$response = `$responseJson | ConvertFrom-Json
            `$result.Success = `$response.Success -eq `$true
            `$result.ProtocolAccepted = `$response.ErrorMessage -ne "SESSION_HOST_PROTOCOL_MISMATCH"
            if (-not `$result.Success) { `$result.SafeErrorCode = if (`$response.ErrorMessage) { [string]`$response.ErrorMessage } else { "PIPE_RESPONSE_FAILED" } }
        }
    } finally {
        `$client.Dispose()
    }
} catch [System.TimeoutException] {
    `$result.SafeErrorCode = "SESSION_HOST_TIMEOUT"
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
        Remove-Item -Path $probeScript -Force -ErrorAction SilentlyContinue
    }
}

function Wait-ForSessionHostCommandPipe {
    param(
        [TimeSpan] $Timeout = [TimeSpan]::FromSeconds(45)
    )

    $deadline = (Get-Date).Add($Timeout)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        $last = Test-SessionHostCommandPipe
        if ($last.Success) {
            return $last
        }
        Start-Sleep -Seconds 2
    }
    return $last
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

function Wait-ForAgentOnline {
    param([TimeSpan] $Timeout)

    $deadline = (Get-Date).Add($Timeout)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        $last = Invoke-E2ECommandIssuer @{ action = "preflight" }
        if ($last.agent.targetAgentConfigured -and $last.agent.isOnline) {
            return $last
        }
        Start-Sleep -Seconds 5
    }
    return $last
}

function Invoke-SecureCommandSmoke {
    param([ValidateSet("PING", "GET_SIM_STATUS")] [string] $CommandType)

    $preflight = Invoke-E2ECommandIssuer @{ action = "preflight" }
    $agentCountBefore = [int]$preflight.agentCount
    if (-not $preflight.agent.targetAgentConfigured -or -not $preflight.agent.isOnline) {
        return [ordered]@{
            Success = $false
            Status = "preflight_failed"
            AttemptCount = $null
            AttemptCountIsOne = $false
            AgentIdentityPreserved = $false
            AgentCountUnchanged = $false
            SanitizedResultReturned = $false
        }
    }

    $action = if ($CommandType -eq "GET_SIM_STATUS") { "create_get_sim_status" } else { "create" }
    $create = Invoke-E2ECommandIssuer @{ action = $action }
    if ([string]::IsNullOrWhiteSpace($create.commandId)) {
        return [ordered]@{
            Success = $false
            Status = "create_failed"
            AttemptCount = $null
            AttemptCountIsOne = $false
            AgentIdentityPreserved = $false
            AgentCountUnchanged = $false
            SanitizedResultReturned = $false
        }
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
        return [ordered]@{
            Success = $false
            Status = "timeout"
            AttemptCount = $null
            AttemptCountIsOne = $false
            AgentIdentityPreserved = $false
            AgentCountUnchanged = $false
            SanitizedResultReturned = $false
        }
    }

    $resultOk = $false
    $sanitized = $false
    if ($CommandType -eq "PING") {
        $resultOk = $final.status -eq "succeeded" -and $final.result.message -eq "pong"
        $sanitized = $resultOk
    } else {
        $resultJson = $final.result | ConvertTo-Json -Depth 12 -Compress
        $resultOk = $final.status -eq "succeeded" -and $final.result.status -eq "succeeded" -and
            ($final.result.PSObject.Properties.Name -contains "applications")
        $sanitized = $resultOk -and
            ($resultJson -notmatch '(?i)C:\\|executablePath|processId|pid|token|secret|credential')
    }

    return [ordered]@{
        Success = [bool]($resultOk -and $sanitized)
        Status = $final.status
        AttemptCount = [int]$final.attemptCount
        AttemptCountIsOne = ([int]$final.attemptCount -eq 1)
        AgentIdentityPreserved = [bool]$final.agent.targetAgentConfigured
        AgentCountUnchanged = ([int]$final.agentCount -eq $agentCountBefore)
        SanitizedResultReturned = [bool]$sanitized
    }
}

function Restore-Backup {
    param(
        [string] $BackupPath,
        [string] $SessionHostBackupPath
    )

    if ([string]::IsNullOrWhiteSpace($BackupPath) -or -not (Test-Path $BackupPath)) {
        return $false
    }

    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
    Stop-SessionHostProcesses

    if (Test-Path $agentDirectory) {
        Remove-Item -Path $agentDirectory -Recurse -Force
    }
    Copy-Item -Path $BackupPath -Destination $agentDirectory -Recurse -Force
    if (-not [string]::IsNullOrWhiteSpace($SessionHostBackupPath) -and (Test-Path $SessionHostBackupPath)) {
        if (Test-Path $sessionHostDirectory) {
            Remove-Item -Path $sessionHostDirectory -Recurse -Force
        }
        Copy-Item -Path $SessionHostBackupPath -Destination $sessionHostDirectory -Recurse -Force
    }
    $interactive = Get-InteractiveUserContext
    if ($interactive) {
        $startupPath = Join-Path $interactive.ProfilePath "AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup"
        New-Directory $startupPath
        $shortcutPath = Join-Path $startupPath $sessionHostShortcutName
        $wsh = New-Object -ComObject WScript.Shell
        $shortcut = $wsh.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $sessionHostExePath
        $shortcut.WorkingDirectory = $sessionHostDirectory
        $shortcut.Save()
    }
    Start-Service -Name $serviceName
    return $true
}

function Stop-SessionHostProcesses {
    Get-Process -Name "SimAgent.SessionHost" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        $remaining = @(Get-Process -Name "SimAgent.SessionHost" -ErrorAction SilentlyContinue)
        if ($remaining.Count -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 250
    }
}

New-Directory $artifactDirectory
$backupPath = Join-Path $backupRoot ("Agent-" + (Get-Date -Format "yyyyMMddHHmmss"))
$sessionHostBackupPath = Join-Path $sessionHostBackupRoot ("SessionHost-" + (Get-Date -Format "yyyyMMddHHmmss"))
$rollbackAttempted = $false
$rollbackSucceeded = $false
$approvedAppsExistedBefore = Test-Path $approvedAppsPath

$validation = [ordered]@{
    Success = $false
    InstalledSimAgentCommit = $InstalledSimAgentCommit
    Backup = [ordered]@{
        ServicePresent = $false
        SessionHostPresent = $false
        SharedAssembliesPresent = $false
        StartupRegistrationCaptured = $false
        ManifestPresent = $false
    }
    PackageConsistency = $null
    PreflightBefore = $null
    PreflightAfter = $null
    ServiceBefore = $null
    ServiceAfter = $null
    LegacyServiceAfter = $null
    AgentSettingsPreserved = $false
    ApprovedAppsPreserved = $false
    SessionHostExecutableExists = $false
    SessionHostStartupConfigured = $false
    SessionHostRunning = $false
    SessionHostInteractiveSession = $false
    SessionHostProcessCount = 0
    PipeConnectionResult = $null
    HandshakeResult = $null
    SecurePingResult = $null
    GetSimStatusResult = $null
    AgentIdentityPreservation = $null
    AgentCountUnchanged = $false
    ServicePathPreserved = $false
    ServiceStartModePreserved = $false
    PayloadExecutableExists = $false
    ServiceVersion = $null
    SessionHostVersion = $null
    CommitConsistencyResult = "not_checked"
    Rollback = [ordered]@{
        Attempted = $false
        Succeeded = $false
        ServiceRunning = $null
        CommitConsistency = $null
    }
    Failures = @()
}

try {
    if (-not (Test-IsAdministrator)) {
        throw "Windows Test Node runner must run elevated for in-place update."
    }
    if (-not (Test-Path $agentSettingsPath)) {
        throw "agentsettings.json is missing; refusing to update because pairing identity must be preserved."
    }
    $agentPayloadDirectory = Join-Path $PayloadDirectory "Agent"
    $sessionHostPayloadDirectory = Join-Path $PayloadDirectory "SessionHost"
    if (-not (Test-Path $agentPayloadDirectory)) {
        $agentPayloadDirectory = $PayloadDirectory
    }
    if (-not (Test-Path $sessionHostPayloadDirectory)) {
        $sessionHostPayloadDirectory = $PayloadDirectory
    }

    if (-not (Test-Path (Join-Path $agentPayloadDirectory "SimAgent.Service.exe"))) {
        throw "Published payload is missing SimAgent.Service.exe."
    }
    if (-not (Test-Path (Join-Path $sessionHostPayloadDirectory "SimAgent.SessionHost.exe"))) {
        throw "Published payload is missing SimAgent.SessionHost.exe."
    }
    $validation.PayloadExecutableExists = $true

    $payloadManifestPath = Join-Path $PayloadDirectory $manifestFileName
    $payloadManifestCommit = Get-PackageManifestCommit $payloadManifestPath
    $payloadServiceVersion = Get-SafeVersionInfo (Join-Path $agentPayloadDirectory "SimAgent.Service.exe")
    $payloadSessionHostVersion = Get-SafeVersionInfo (Join-Path $sessionHostPayloadDirectory "SimAgent.SessionHost.exe")
    $expectedCommit = $InstalledSimAgentCommit.ToLowerInvariant()
    $validation.PackageConsistency = [ordered]@{
        ServiceCommit = $payloadServiceVersion.Commit
        SessionHostCommit = $payloadSessionHostVersion.Commit
        ManifestCommit = $payloadManifestCommit
        ServiceSharedAssembliesPresent = Test-SharedAssemblies $agentPayloadDirectory
        SessionHostSharedAssembliesPresent = Test-SharedAssemblies $sessionHostPayloadDirectory
    }
    if ($payloadServiceVersion.Commit -ne $expectedCommit -or
        $payloadSessionHostVersion.Commit -ne $expectedCommit -or
        $payloadManifestCommit -ne $expectedCommit) {
        throw "Payload commit markers do not match the requested SimAgent commit."
    }
    if (-not $validation.PackageConsistency.ServiceSharedAssembliesPresent -or
        -not $validation.PackageConsistency.SessionHostSharedAssembliesPresent) {
        throw "Payload is missing required shared assemblies."
    }

    $preflightBefore = Wait-ForAgentOnline ([TimeSpan]::FromSeconds(90))
    $validation.PreflightBefore = $preflightBefore
    if (-not $preflightBefore -or -not $preflightBefore.agent.targetAgentConfigured) {
        throw "Configured E2E target Agent was not found before update."
    }

    $serviceBefore = Get-ServiceSnapshot
    $validation.ServiceBefore = $serviceBefore
    if (-not $serviceBefore.Exists) {
        throw "SimAgentService is missing."
    }
    if (-not $serviceBefore.CanonicalExecutable) {
        throw "SimAgentService does not use the canonical SimAgent.Service executable and config argument."
    }
    if (-not (Test-Path $agentDirectory)) {
        throw "Agent binary directory is missing."
    }

    New-Directory $backupRoot
    Copy-Item -Path $agentDirectory -Destination $backupPath -Recurse -Force
    if (Test-Path $sessionHostDirectory) {
        New-Directory $sessionHostBackupRoot
        Copy-Item -Path $sessionHostDirectory -Destination $sessionHostBackupPath -Recurse -Force
    }
    $currentServiceVersion = Get-SafeVersionInfo $agentExePath
    if ($currentServiceVersion.Commit) {
        Write-VersionManifest $backupPath $currentServiceVersion.Commit $currentServiceVersion.ProductVersion
        if (Test-Path $sessionHostBackupPath) {
            Write-VersionManifest $sessionHostBackupPath $currentServiceVersion.Commit $currentServiceVersion.ProductVersion
        }
    }
    $interactiveUser = Get-InteractiveUserContext
    if ($null -eq $interactiveUser) {
        throw "Unable to resolve logged-in interactive user."
    }
    $startupPath = Join-Path $interactiveUser.ProfilePath "AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup"
    $startupShortcutPath = Join-Path $startupPath $sessionHostShortcutName
    $backupStartupDirectory = Join-Path $sessionHostBackupPath "startup-assets"
    if (Test-Path $startupShortcutPath) {
        New-Directory $backupStartupDirectory
        Copy-Item -Path $startupShortcutPath -Destination (Join-Path $backupStartupDirectory $sessionHostShortcutName) -Force
        $validation.Backup.StartupRegistrationCaptured = $true
    }
    $validation.Backup.ServicePresent = Test-Path (Join-Path $backupPath "SimAgent.Service.exe")
    $validation.Backup.SessionHostPresent = Test-Path (Join-Path $sessionHostBackupPath "SimAgent.SessionHost.exe")
    $validation.Backup.SharedAssembliesPresent = (Test-SharedAssemblies $backupPath) -and (Test-SharedAssemblies $sessionHostBackupPath)
    $validation.Backup.ManifestPresent = (Test-Path (Join-Path $backupPath $manifestFileName)) -and (Test-Path (Join-Path $sessionHostBackupPath $manifestFileName))

    Stop-SessionHostProcesses

    if ($serviceBefore.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
        (Get-Service -Name $serviceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }

    Get-ChildItem -Path $agentDirectory -Force | Remove-Item -Recurse -Force
    Copy-Item -Path (Join-Path $agentPayloadDirectory "*") -Destination $agentDirectory -Recurse -Force
    Copy-Item -Path $payloadManifestPath -Destination (Join-Path $agentDirectory $manifestFileName) -Force
    if (Test-Path $sessionHostDirectory) {
        Get-ChildItem -Path $sessionHostDirectory -Force | Remove-Item -Recurse -Force
    } else {
        New-Directory $sessionHostDirectory
    }
    Copy-Item -Path (Join-Path $sessionHostPayloadDirectory "*") -Destination $sessionHostDirectory -Recurse -Force
    Copy-Item -Path $payloadManifestPath -Destination (Join-Path $sessionHostDirectory $manifestFileName) -Force

    if (-not (Test-Path $agentSettingsPath)) {
        throw "agentsettings.json disappeared during payload update."
    }
    $validation.AgentSettingsPreserved = $true
    $validation.ApprovedAppsPreserved = (-not $approvedAppsExistedBefore) -or (Test-Path $approvedAppsPath)
    if (-not (Test-Path $sessionHostExePath)) {
        throw "SimAgent.SessionHost.exe is missing after payload update."
    }
    $validation.SessionHostExecutableExists = $true

    New-Directory $startupPath
    $shortcutPath = Join-Path $startupPath $sessionHostShortcutName
    $wsh = New-Object -ComObject WScript.Shell
    $shortcut = $wsh.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $sessionHostExePath
    $shortcut.WorkingDirectory = $sessionHostDirectory
    $shortcut.Save()
    $validation.SessionHostStartupConfigured = Test-Path $shortcutPath

    Start-SessionHostForInteractiveUser $interactiveUser.UserName
    Start-Sleep -Seconds 5

    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
    Start-Sleep -Seconds 5

    $serviceAfter = Get-ServiceSnapshot
    $legacyAfter = Get-ServiceSnapshot $legacyServiceName
    $preflightAfter = Wait-ForAgentOnline ([TimeSpan]::FromSeconds(120))

    $validation.ServiceAfter = $serviceAfter
    $validation.LegacyServiceAfter = $legacyAfter
    $validation.PreflightAfter = $preflightAfter
    $validation.AgentCountUnchanged = $null -ne $preflightAfter -and [int]$preflightAfter.agentCount -eq [int]$preflightBefore.agentCount
    $validation.ServicePathPreserved = $serviceAfter.CanonicalExecutable -eq $true -and $serviceBefore.CanonicalExecutable -eq $true
    $validation.ServiceStartModePreserved = $serviceAfter.StartMode -eq $serviceBefore.StartMode
    $sessionHostProcesses = @(Get-Process -Name "SimAgent.SessionHost" -ErrorAction SilentlyContinue)
    $validation.SessionHostProcessCount = $sessionHostProcesses.Count
    $validation.SessionHostRunning = $sessionHostProcesses.Count -gt 0
    $validation.SessionHostInteractiveSession = @($sessionHostProcesses | Where-Object { $_.SessionId -gt 0 }).Count -gt 0
    $serviceVersion = Get-SafeVersionInfo $agentExePath
    $sessionHostVersion = Get-SafeVersionInfo $sessionHostExePath
    $installedManifestCommit = Get-PackageManifestCommit (Join-Path $agentDirectory $manifestFileName)
    $validation.ServiceVersion = [ordered]@{ Commit = $serviceVersion.Commit; ProductVersion = $serviceVersion.ProductVersion }
    $validation.SessionHostVersion = [ordered]@{ Commit = $sessionHostVersion.Commit; ProductVersion = $sessionHostVersion.ProductVersion }
    if ($serviceVersion.Commit -eq $expectedCommit -and
        $sessionHostVersion.Commit -eq $expectedCommit -and
        $installedManifestCommit -eq $expectedCommit) {
        $validation.CommitConsistencyResult = "passed"
    } else {
        $validation.CommitConsistencyResult = "failed"
        throw "Installed Service, SessionHost, and manifest commits do not match."
    }

    $runnerPipe = Wait-ForSessionHostCommandPipe
    $systemPipe = Invoke-SystemPipeProbe
    $validation.PipeConnectionResult = [ordered]@{
        RunnerCanConnect = $runnerPipe.CanConnect
        LocalSystemCanConnect = $systemPipe.CanConnect
    }
    $validation.HandshakeResult = [ordered]@{
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

    if ($serviceAfter.Status -ne "Running") {
        throw "SimAgentService is not Running after update."
    }
    if (-not $validation.SessionHostRunning) {
        throw "SimAgent.SessionHost is not running after update."
    }
    if (-not $validation.SessionHostInteractiveSession) {
        throw "SimAgent.SessionHost is not running in an interactive session."
    }
    if ($validation.SessionHostProcessCount -ne 1) {
        throw "SimAgent.SessionHost process count is not one."
    }
    if (-not $runnerPipe.Success -or -not $systemPipe.Success) {
        throw "SimAgent.SessionHost command pipe handshake failed."
    }
    if ($legacyAfter.Exists) {
        throw "Legacy SimBootstrapAgent service exists after update."
    }
    if ($null -eq $preflightAfter -or -not $preflightAfter.agent.targetAgentConfigured -or -not $preflightAfter.agent.isOnline) {
        throw "Agent heartbeat did not become online/recent after update."
    }
    if (-not $validation.AgentCountUnchanged) {
        throw "Agent count changed during update."
    }
    if (-not $validation.ServicePathPreserved -or -not $validation.ServiceStartModePreserved) {
        throw "Service registration changed during update."
    }

    $ping = Invoke-SecureCommandSmoke "PING"
    $validation.SecurePingResult = $ping
    $validation.AgentIdentityPreservation = [ordered]@{
        AgentIdentityPreserved = $ping.AgentIdentityPreserved
        AgentCountUnchanged = $ping.AgentCountUnchanged
    }
    if (-not $ping.Success -or -not $ping.AttemptCountIsOne -or -not $ping.AgentIdentityPreserved -or -not $ping.AgentCountUnchanged) {
        throw "Secure PING failed after update."
    }

    $simStatus = Invoke-SecureCommandSmoke "GET_SIM_STATUS"
    $validation.GetSimStatusResult = $simStatus
    if (-not $simStatus.Success -or -not $simStatus.AttemptCountIsOne -or -not $simStatus.SanitizedResultReturned) {
        throw "GET_SIM_STATUS failed after update."
    }

    $validation.Success = $true
} catch {
    $validation.Failures = @([string]$_.Exception.Message)
    try {
        $rollbackAttempted = $true
        $rollbackSucceeded = Restore-Backup $backupPath $sessionHostBackupPath
        $validation.ServiceAfter = Get-ServiceSnapshot
        $validation.PreflightAfter = Wait-ForAgentOnline ([TimeSpan]::FromSeconds(90))
        $rollbackServiceVersion = Get-SafeVersionInfo $agentExePath
        $rollbackSessionHostVersion = Get-SafeVersionInfo $sessionHostExePath
        $rollbackService = Get-ServiceSnapshot
        $validation.Rollback.ServiceRunning = $rollbackService.Status -eq "Running"
        $validation.Rollback.CommitConsistency = $rollbackServiceVersion.Commit -eq $rollbackSessionHostVersion.Commit
    } catch {
        $validation.Failures += "Rollback failed: $($_.Exception.Message)"
    }
    $validation.Rollback.Attempted = $rollbackAttempted
    $validation.Rollback.Succeeded = $rollbackSucceeded
}

Write-JsonFile $validation $validationReportPath
Write-JsonFile $validation (Join-Path $artifactDirectory "validation-report.json")

try {
    $recentLogs = @(
        Get-ChildItem -Path (Join-Path $programDataRoot "logs") -File -ErrorAction SilentlyContinue
        Get-ChildItem -Path (Join-Path $agentDirectory "logs") -File -ErrorAction SilentlyContinue
        Get-ChildItem -Path (Join-Path $sessionHostDirectory "logs") -File -ErrorAction SilentlyContinue
    ) | Where-Object { $_.LastWriteTime -gt (Get-Date).AddMinutes(-15) } | Select-Object -First 10
    foreach ($log in $recentLogs) {
        $destination = Join-Path $artifactDirectory ("logs\" + $log.Name + ".redacted.txt")
        New-Directory (Split-Path -Parent $destination)
        ConvertTo-RedactedText (Get-Content -Raw -Path $log.FullName) | Set-Content -Path $destination -Encoding UTF8
    }
} catch {}

if (-not $validation.Success) {
    throw "In-place SimAgent update failed: $($validation.Failures -join '; ')"
}

Write-Host "In-place SimAgent update passed."
