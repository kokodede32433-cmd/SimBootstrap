[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $RunId,

    [string] $RootDirectory = "C:\SimPlatformTestNode",

    [string] $ApplicationId = "simhub"
)

$ErrorActionPreference = "Stop"

$serviceName = "SimAgentService"
$programDataRoot = "C:\ProgramData\SimBootstrap"
$agentSettingsPath = Join-Path $programDataRoot "config\agentsettings.json"
$approvedAppsPath = Join-Path $programDataRoot "config\approved-apps.json"
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
    $Value | ConvertTo-Json -Depth 16 | Set-Content -Path $Path -Encoding UTF8
}

function Assert-RequiredSecret {
    param(
        [Parameter(Mandatory = $true)] [string] $Name,
        [string] $Value
    )
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "Missing required secret/environment value: $Name"
    }
    Write-Host "::add-mask::$Value"
}

function Get-InteractiveUserName {
    $explorerProcesses = @(
        Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.SessionId -gt 0 }
    )

    foreach ($process in $explorerProcesses) {
        $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwner -ErrorAction SilentlyContinue
        if ($null -eq $owner -or [string]::IsNullOrWhiteSpace($owner.User)) {
            continue
        }

        if ([string]::IsNullOrWhiteSpace($owner.Domain)) {
            return $owner.User
        }

        return "$($owner.Domain)\$($owner.User)"
    }

    return $null
}

function Get-PrincipalNameFromSid {
    param([Parameter(Mandatory = $true)] [string] $Sid)
    $sidObject = New-Object System.Security.Principal.SecurityIdentifier($Sid)
    return $sidObject.Translate([System.Security.Principal.NTAccount]).Value
}

function Test-RuleAllowsRead {
    param($Rule)
    $rights = [System.Security.AccessControl.FileSystemRights] $Rule.FileSystemRights
    return (($rights -band [System.Security.AccessControl.FileSystemRights]::Read) -ne 0) -or
        (($rights -band [System.Security.AccessControl.FileSystemRights]::ReadAndExecute) -ne 0)
}

function Test-RuleAllowsBroadWrite {
    param($Rule)
    $rights = [System.Security.AccessControl.FileSystemRights] $Rule.FileSystemRights
    return (($rights -band [System.Security.AccessControl.FileSystemRights]::FullControl) -ne 0) -or
        (($rights -band [System.Security.AccessControl.FileSystemRights]::Modify) -ne 0) -or
        (($rights -band [System.Security.AccessControl.FileSystemRights]::Write) -ne 0)
}

function Test-PrincipalHasRead {
    param(
        [Parameter(Mandatory = $true)] $Acl,
        [Parameter(Mandatory = $true)] [string] $Identity
    )
    $rules = $Acl.GetAccessRules($true, $true, [System.Security.Principal.NTAccount])
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -eq "Allow" -and
            [string]::Equals($rule.IdentityReference.Value, $Identity, [System.StringComparison]::OrdinalIgnoreCase) -and
            (Test-RuleAllowsRead $rule)) {
            return $true
        }
    }
    return $false
}

function Test-PrincipalHasBroadAccess {
    param(
        [Parameter(Mandatory = $true)] $Acl,
        [Parameter(Mandatory = $true)] [string] $Identity
    )
    $rules = $Acl.GetAccessRules($true, $true, [System.Security.Principal.NTAccount])
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -eq "Allow" -and
            [string]::Equals($rule.IdentityReference.Value, $Identity, [System.StringComparison]::OrdinalIgnoreCase) -and
            (Test-RuleAllowsBroadWrite $rule)) {
            return $true
        }
    }
    return $false
}

function Invoke-StatusPoll {
    param(
        [Parameter(Mandatory = $true)] [string] $CommandId,
        [Parameter(Mandatory = $true)] [hashtable] $Headers,
        [Parameter(Mandatory = $true)] [string] $SupabaseUrl,
        [int] $TimeoutSeconds = 180
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $statusBody = @{ p_command_id = $CommandId } | ConvertTo-Json -Compress
        $statusResponse = Invoke-RestMethod -Method Post -Uri "$SupabaseUrl/rest/v1/rpc/get_agent_command_status_v1" -Headers $Headers -Body $statusBody
        if ($statusResponse.status -in @("succeeded", "failed", "expired", "cancelled")) {
            return $statusResponse
        }
        Start-Sleep -Seconds 5
    }

    throw "Command timed out."
}

function Get-AttemptCount {
    param(
        [Parameter(Mandatory = $true)] [string] $CommandId,
        [Parameter(Mandatory = $true)] [hashtable] $Headers,
        [Parameter(Mandatory = $true)] [string] $SupabaseUrl
    )
    $queryUrl = "$SupabaseUrl/rest/v1/agent_commands?id=eq.$CommandId&select=attempt_count"
    $queryResult = Invoke-RestMethod -Method Get -Uri $queryUrl -Headers $Headers
    if ($null -eq $queryResult -or $queryResult.Count -eq 0) {
        throw "Unable to resolve attempt_count."
    }
    return [int]$queryResult[0].attempt_count
}

function New-AgentCommand {
    param(
        [Parameter(Mandatory = $true)] [string] $AgentId,
        [Parameter(Mandatory = $true)] [string] $CommandType,
        [Parameter(Mandatory = $true)] [hashtable] $Headers,
        [Parameter(Mandatory = $true)] [string] $SupabaseUrl,
        [hashtable] $Payload
    )

    $body = @{
        p_agent_id = $AgentId
        p_command_type = $CommandType
    }
    if ($null -ne $Payload) {
        $body.p_payload = $Payload
    }

    $response = Invoke-RestMethod -Method Post -Uri "$SupabaseUrl/rest/v1/rpc/create_agent_command_v1" -Headers $Headers -Body ($body | ConvertTo-Json -Compress)
    if ([string]::IsNullOrWhiteSpace($response.command_id)) {
        throw "Command creation did not return an id."
    }
    Write-Host "::add-mask::$($response.command_id)"
    return $response.command_id
}

function Invoke-AgentCommandAndWait {
    param(
        [Parameter(Mandatory = $true)] [string] $AgentId,
        [Parameter(Mandatory = $true)] [string] $CommandType,
        [Parameter(Mandatory = $true)] [hashtable] $Headers,
        [Parameter(Mandatory = $true)] [string] $SupabaseUrl,
        [hashtable] $Payload
    )

    $commandId = New-AgentCommand -AgentId $AgentId -CommandType $CommandType -Headers $Headers -SupabaseUrl $SupabaseUrl -Payload $Payload
    $final = Invoke-StatusPoll -CommandId $commandId -Headers $Headers -SupabaseUrl $SupabaseUrl
    $attemptCount = Get-AttemptCount -CommandId $commandId -Headers $Headers -SupabaseUrl $SupabaseUrl
    return [ordered]@{
        CommandId = $commandId
        Status = $final.status
        Result = $final.result
        AttemptCount = $attemptCount
    }
}

function Test-SanitizedResult {
    param($Result)
    $json = $Result | ConvertTo-Json -Depth 16 -Compress
    return $json -notmatch '(?i)\\|/|executablePath|workingDirectory|processId|pid|token|secret|credential|authorization|username|owner|windowTitle|commandLine'
}

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SimBootstrapWindowProbe {
    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);
}
"@

function Get-AppProcessEvidence {
    param(
        [Parameter(Mandatory = $true)] [string[]] $ProcessNames,
        [string] $ExpectedOwner
    )

    $processes = @()
    foreach ($name in $ProcessNames) {
        $processes += @(Get-Process -Name $name -ErrorAction SilentlyContinue)
    }
    $processes = @($processes | Sort-Object Id -Unique)

    $visibleWindows = 0
    $sessionIds = @()
    $ownersMatch = $true
    foreach ($process in $processes) {
        $sessionIds += $process.SessionId
        if ($process.MainWindowHandle -ne [IntPtr]::Zero -and [SimBootstrapWindowProbe]::IsWindowVisible($process.MainWindowHandle)) {
            $visibleWindows++
        }

        if (-not [string]::IsNullOrWhiteSpace($ExpectedOwner)) {
            $cim = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)" -ErrorAction SilentlyContinue
            if ($null -ne $cim) {
                $owner = Invoke-CimMethod -InputObject $cim -MethodName GetOwner -ErrorAction SilentlyContinue
                $actualOwner = if ([string]::IsNullOrWhiteSpace($owner.Domain)) { $owner.User } else { "$($owner.Domain)\$($owner.User)" }
                if (-not [string]::Equals($actualOwner, $ExpectedOwner, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $ownersMatch = $false
                }
            }
        }
    }

    $interactiveCount = @($processes | Where-Object { $_.SessionId -ge 1 }).Count
    return [ordered]@{
        ProcessCount = $processes.Count
        InteractiveProcessCount = $interactiveCount
        VisibleWindowCount = $visibleWindows
        SessionIdsAreInteractive = ($processes.Count -gt 0 -and $interactiveCount -eq $processes.Count)
        OwnerIsInteractiveUser = ($processes.Count -gt 0 -and $ownersMatch)
    }
}

try {
    New-Directory $artifactDirectory

    if ($ApplicationId -ne "simhub") {
        throw "This workflow is pinned to simhub for the staging lifecycle smoke."
    }

    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service -or $service.Status -ne "Running") {
        throw "SimAgentService is not running."
    }
    $serviceInfo = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
    $serviceProcess = if ($null -ne $serviceInfo -and $serviceInfo.ProcessId -gt 0) {
        Get-CimInstance Win32_Process -Filter "ProcessId=$($serviceInfo.ProcessId)" -ErrorAction SilentlyContinue
    } else {
        $null
    }

    if (-not (Test-Path $agentSettingsPath)) {
        throw "agentsettings.json is missing."
    }
    $settings = Get-Content -Path $agentSettingsPath -Raw | ConvertFrom-Json
    $agentId = $settings.agentId
    if ([string]::IsNullOrWhiteSpace($agentId)) {
        throw "agentId is empty."
    }

    if (-not (Test-Path $approvedAppsPath)) {
        throw "approved-apps.json is missing."
    }

    $interactiveUser = Get-InteractiveUserName
    if ([string]::IsNullOrWhiteSpace($interactiveUser)) {
        throw "Unable to resolve logged-in interactive user."
    }

    $systemUser = Get-PrincipalNameFromSid "S-1-5-18"
    $adminsUser = Get-PrincipalNameFromSid "S-1-5-32-544"
    $everyoneUser = Get-PrincipalNameFromSid "S-1-1-0"
    $usersUser = Get-PrincipalNameFromSid "S-1-5-32-545"

    $acl = Get-Acl -LiteralPath $approvedAppsPath
    $aclChanged = $false
    if (-not (Test-PrincipalHasRead -Acl $acl -Identity $interactiveUser)) {
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($interactiveUser, "ReadAndExecute", "Allow")))
        Set-Acl -LiteralPath $approvedAppsPath -AclObject $acl
        $acl = Get-Acl -LiteralPath $approvedAppsPath
        $aclChanged = $true
    }

    $aclResult = [ordered]@{
        SessionHostCanRead = $false
        LocalSystemRead = (Test-PrincipalHasRead -Acl $acl -Identity $systemUser)
        AdministratorsRead = (Test-PrincipalHasRead -Acl $acl -Identity $adminsUser)
        InteractiveUserReadOnly = ((Test-PrincipalHasRead -Acl $acl -Identity $interactiveUser) -and -not (Test-PrincipalHasBroadAccess -Acl $acl -Identity $interactiveUser))
        EveryoneBroadAccess = (Test-PrincipalHasBroadAccess -Acl $acl -Identity $everyoneUser)
        UsersBroadAccess = (Test-PrincipalHasBroadAccess -Acl $acl -Identity $usersUser)
        MinimalCorrectionApplied = $aclChanged
    }
    if (-not $aclResult.LocalSystemRead -or
        -not $aclResult.AdministratorsRead -or
        -not $aclResult.InteractiveUserReadOnly -or
        $aclResult.EveryoneBroadAccess -or
        $aclResult.UsersBroadAccess) {
        throw "APPROVED_APPS_ACL_VALIDATION_FAILED"
    }

    $approvedApps = Get-Content -Path $approvedAppsPath -Raw | ConvertFrom-Json
    $appConfigProperty = $approvedApps.applications.PSObject.Properties[$ApplicationId]
    $appConfig = if ($null -ne $appConfigProperty) { $appConfigProperty.Value } else { $null }
    if ($null -eq $appConfig) {
        throw "simhub approved app record is missing."
    }
    $enabledProperty = $appConfig.PSObject.Properties["enabled"]
    $enabled = $true
    if ($null -ne $enabledProperty) {
        $enabled = [bool]$enabledProperty.Value
    }
    if (-not $enabled) {
        throw "simhub approved app record is disabled."
    }
    if ([string]::IsNullOrWhiteSpace($appConfig.executablePath) -or -not (Test-Path $appConfig.executablePath)) {
        throw "simhub executable is not available."
    }
    $processNames = @($appConfig.processNames)
    if ($processNames.Count -eq 0 -or @($processNames | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        throw "simhub processNames are invalid."
    }

    $validationResult = [ordered]@{
        ApprovedAppId = $ApplicationId
        Enabled = $enabled
        LaunchMode = "SessionHostInteractive"
        GracefulStopSupport = "WM_CLOSE"
        StatusDetectionStrategy = "configuredProcessNames"
    }

    Assert-RequiredSecret "SIMCRM_STAGING_SUPABASE_URL" $env:SIMCRM_STAGING_SUPABASE_URL
    Assert-RequiredSecret "SIMCRM_STAGING_SUPABASE_ANON_KEY" $env:SIMCRM_STAGING_SUPABASE_ANON_KEY
    Assert-RequiredSecret "SIMCRM_STAGING_PAIR_CODE_TOKEN" $env:SIMCRM_STAGING_PAIR_CODE_TOKEN
    Assert-RequiredSecret "E2E_EMAIL" $env:E2E_EMAIL
    Assert-RequiredSecret "E2E_PASSWORD" $env:E2E_PASSWORD

    $supabaseUrl = $env:SIMCRM_STAGING_SUPABASE_URL.TrimEnd("/")
    $anonKey = $env:SIMCRM_STAGING_SUPABASE_ANON_KEY

    $e2eHeaders = @{
        apikey = $anonKey
        Authorization = "Bearer $($env:SIMCRM_STAGING_PAIR_CODE_TOKEN)"
        "Content-Type" = "application/json"
    }
    $preflight = Invoke-RestMethod -Method Post -Uri "$supabaseUrl/functions/v1/create-e2e-agent-command" -Headers $e2eHeaders -Body (@{ action = "preflight" } | ConvertTo-Json -Compress)
    if (-not $preflight.agent.targetAgentConfigured -or -not $preflight.agent.isOnline) {
        throw "Target staging Agent is not configured and online."
    }
    $agentCountBefore = [int]$preflight.agentCount

    $authHeaders = @{
        apikey = $anonKey
        "Content-Type" = "application/json"
    }
    $authResponse = Invoke-RestMethod -Method Post -Uri "$supabaseUrl/auth/v1/token?grant_type=password" -Headers $authHeaders -Body (@{
        email = $env:E2E_EMAIL
        password = $env:E2E_PASSWORD
    } | ConvertTo-Json -Compress)
    $jwt = $authResponse.access_token
    if ([string]::IsNullOrWhiteSpace($jwt)) {
        throw "Failed to obtain staging JWT."
    }
    Write-Host "::add-mask::$jwt"

    $rpcHeaders = @{
        apikey = $anonKey
        Authorization = "Bearer $jwt"
        "Content-Type" = "application/json"
    }

    $beforeEvidence = Get-AppProcessEvidence -ProcessNames $processNames -ExpectedOwner $interactiveUser
    if ($beforeEvidence.ProcessCount -gt 0) {
        throw "PREEXISTING_APPROVED_APP_PROCESS"
    }

    $start = Invoke-AgentCommandAndWait -AgentId $agentId -CommandType "START_APPROVED_APP" -Headers $rpcHeaders -SupabaseUrl $supabaseUrl -Payload @{ applicationId = $ApplicationId }
    if ($start.Status -ne "succeeded" -or $start.Result.status -ne "started" -or $start.AttemptCount -ne 1 -or -not (Test-SanitizedResult $start.Result)) {
        throw "START_APPROVED_APP_VALIDATION_FAILED"
    }

    $launchDeadline = (Get-Date).AddSeconds(60)
    $launchEvidence = $null
    while ((Get-Date) -lt $launchDeadline) {
        $launchEvidence = Get-AppProcessEvidence -ProcessNames $processNames -ExpectedOwner $interactiveUser
        if ($launchEvidence.ProcessCount -ge 1 -and
            $launchEvidence.SessionIdsAreInteractive -and
            $launchEvidence.OwnerIsInteractiveUser -and
            $launchEvidence.VisibleWindowCount -ge 1) {
            break
        }
        Start-Sleep -Seconds 3
    }
    if ($launchEvidence.ProcessCount -lt 1 -or -not $launchEvidence.SessionIdsAreInteractive -or -not $launchEvidence.OwnerIsInteractiveUser) {
        throw "INTERACTIVE_PROCESS_VALIDATION_FAILED"
    }
    if ($launchEvidence.VisibleWindowCount -lt 1) {
        throw "VISIBLE_WINDOW_VALIDATION_FAILED"
    }
    $aclResult.SessionHostCanRead = $true

    $firstStatus = Invoke-AgentCommandAndWait -AgentId $agentId -CommandType "GET_SIM_STATUS" -Headers $rpcHeaders -SupabaseUrl $supabaseUrl
    $firstStatusApp = @($firstStatus.Result.applications | Where-Object { $_.id -eq $ApplicationId }) | Select-Object -First 1
    if ($firstStatus.Status -ne "succeeded" -or $firstStatus.AttemptCount -ne 1 -or $null -eq $firstStatusApp -or -not $firstStatusApp.running -or -not (Test-SanitizedResult $firstStatus.Result)) {
        throw "FIRST_GET_SIM_STATUS_VALIDATION_FAILED"
    }

    $secondStart = Invoke-AgentCommandAndWait -AgentId $agentId -CommandType "START_APPROVED_APP" -Headers $rpcHeaders -SupabaseUrl $supabaseUrl -Payload @{ applicationId = $ApplicationId }
    if ($secondStart.Status -ne "succeeded" -or $secondStart.Result.status -ne "already_running" -or $secondStart.AttemptCount -ne 1 -or -not (Test-SanitizedResult $secondStart.Result)) {
        throw "ALREADY_RUNNING_VALIDATION_FAILED"
    }

    Start-Sleep -Seconds 4
    $afterSecondStartEvidence = Get-AppProcessEvidence -ProcessNames $processNames -ExpectedOwner $interactiveUser
    if ($afterSecondStartEvidence.ProcessCount -ne $launchEvidence.ProcessCount -or $afterSecondStartEvidence.VisibleWindowCount -ne $launchEvidence.VisibleWindowCount) {
        throw "DUPLICATE_PROCESS_OR_WINDOW_DETECTED"
    }

    $stop = Invoke-AgentCommandAndWait -AgentId $agentId -CommandType "STOP_APPROVED_APP" -Headers $rpcHeaders -SupabaseUrl $supabaseUrl -Payload @{ applicationId = $ApplicationId }
    if ($stop.Status -ne "succeeded" -or $stop.Result.status -ne "stopped" -or $stop.AttemptCount -ne 1 -or -not (Test-SanitizedResult $stop.Result)) {
        throw "STOP_APPROVED_APP_VALIDATION_FAILED"
    }

    $stopDeadline = (Get-Date).AddSeconds(30)
    $afterStopEvidence = $null
    while ((Get-Date) -lt $stopDeadline) {
        $afterStopEvidence = Get-AppProcessEvidence -ProcessNames $processNames -ExpectedOwner $interactiveUser
        if ($afterStopEvidence.ProcessCount -eq 0) {
            break
        }
        Start-Sleep -Seconds 2
    }
    if ($null -eq $afterStopEvidence -or $afterStopEvidence.ProcessCount -ne 0) {
        throw "GRACEFUL_SHUTDOWN_VALIDATION_FAILED"
    }

    $secondStatus = Invoke-AgentCommandAndWait -AgentId $agentId -CommandType "GET_SIM_STATUS" -Headers $rpcHeaders -SupabaseUrl $supabaseUrl
    $secondStatusApp = @($secondStatus.Result.applications | Where-Object { $_.id -eq $ApplicationId }) | Select-Object -First 1
    if ($secondStatus.Status -ne "succeeded" -or $secondStatus.AttemptCount -ne 1 -or ($null -ne $secondStatusApp -and $secondStatusApp.running) -or -not (Test-SanitizedResult $secondStatus.Result)) {
        throw "SECOND_GET_SIM_STATUS_VALIDATION_FAILED"
    }

    $ping = Invoke-AgentCommandAndWait -AgentId $agentId -CommandType "PING" -Headers $rpcHeaders -SupabaseUrl $supabaseUrl
    if ($ping.Status -ne "succeeded" -or $ping.Result.message -ne "pong" -or $ping.AttemptCount -ne 1 -or -not (Test-SanitizedResult $ping.Result)) {
        throw "FINAL_PING_VALIDATION_FAILED"
    }
    $preflightAfter = Invoke-RestMethod -Method Post -Uri "$supabaseUrl/functions/v1/create-e2e-agent-command" -Headers $e2eHeaders -Body (@{ action = "preflight" } | ConvertTo-Json -Compress)
    if (-not $preflightAfter.agent.targetAgentConfigured -or [int]$preflightAfter.agentCount -ne $agentCountBefore -or -not $preflightAfter.agent.isOnline) {
        throw "AGENT_IDENTITY_OR_COUNT_CHANGED"
    }

    $serviceAfter = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $serviceAfter -or $serviceAfter.Status -ne "Running") {
        throw "SERVICE_NOT_HEALTHY_AFTER_LIFECYCLE"
    }
    $sessionHostCount = @(Get-Process -Name "SimAgent.SessionHost" -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -ge 1 }).Count
    if ($sessionHostCount -ne 1) {
        throw "SESSIONHOST_NOT_HEALTHY_AFTER_LIFECYCLE"
    }

    $report = [ordered]@{
        Success = $true
        Stage = "APPROVED_APP_LIFECYCLE"
        ApprovedAppsAclResult = $aclResult
        ApprovedAppValidationResult = $validationResult
        StartApprovedAppResult = @{
            Status = $start.Status
            ResultStatus = $start.Result.status
            AttemptCount = $start.AttemptCount
            SanitizedResult = $true
        }
        ServiceForwardingResult = @{
            ServiceSessionId = if ($null -ne $serviceProcess) { $serviceProcess.SessionId } else { 0 }
            ForwardedToSessionHost = $true
        }
        SessionHostLaunchResult = @{
            LaunchMode = "SessionHost"
            SessionHostProcessCount = $sessionHostCount
            ResultStatus = $start.Result.status
        }
        InteractiveSessionResult = $launchEvidence
        VisibleWindowResult = @{
            MainWindowExists = ($launchEvidence.VisibleWindowCount -gt 0)
            VisibleWindowCount = $launchEvidence.VisibleWindowCount
        }
        FirstGetSimStatusResult = @{
            Status = $firstStatus.Status
            SimhubRunning = [bool]$firstStatusApp.running
            AttemptCount = $firstStatus.AttemptCount
            SanitizedResult = $true
        }
        AlreadyRunningResult = @{
            Status = $secondStart.Status
            ResultStatus = $secondStart.Result.status
            AttemptCount = $secondStart.AttemptCount
            SanitizedResult = $true
        }
        DuplicateProcessCheck = @{
            BeforeCount = $launchEvidence.ProcessCount
            AfterCount = $afterSecondStartEvidence.ProcessCount
            BeforeVisibleWindowCount = $launchEvidence.VisibleWindowCount
            AfterVisibleWindowCount = $afterSecondStartEvidence.VisibleWindowCount
            DuplicateDetected = $false
        }
        StopApprovedAppResult = @{
            Status = $stop.Status
            ResultStatus = $stop.Result.status
            AttemptCount = $stop.AttemptCount
            SanitizedResult = $true
        }
        GracefulShutdownResult = @{
            ProcessCountAfterStop = $afterStopEvidence.ProcessCount
            NoOrphanProcess = $true
            ForceKillUsed = $false
        }
        SecondGetSimStatusResult = @{
            Status = $secondStatus.Status
            SimhubRunning = if ($null -eq $secondStatusApp) { $false } else { [bool]$secondStatusApp.running }
            AttemptCount = $secondStatus.AttemptCount
            SanitizedResult = $true
            ServiceHealthy = $true
            SessionHostHealthy = $true
            FinalPingPong = $true
            AgentIdPreserved = [bool]$preflightAfter.agent.targetAgentConfigured
            AgentCountUnchanged = ([int]$preflightAfter.agentCount -eq $agentCountBefore)
        }
    }

    Write-JsonFile $report $validationReportPath
    Write-JsonFile $report (Join-Path $artifactDirectory "validation-report.json")
    Write-Host "Approved app lifecycle E2E passed."
} catch {
    Write-Host "ERROR: $_"
    $failureReport = [ordered]@{
        Success = $false
        Stage = "APPROVED_APP_LIFECYCLE"
        ErrorCode = "$_"
    }
    New-Directory $artifactDirectory
    Write-JsonFile $failureReport $validationReportPath
    Write-JsonFile $failureReport (Join-Path $artifactDirectory "validation-report.json")
    exit 1
}
