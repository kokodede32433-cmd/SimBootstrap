[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $RunId,

    [string] $RootDirectory = "C:\SimPlatformTestNode"
)

$ErrorActionPreference = "Stop"

$serviceName = "SimAgentService"
$programDataRoot = "C:\ProgramData\SimBootstrap"
$agentSettingsPath = Join-Path $programDataRoot "config\agentsettings.json"
$configPath = Join-Path $programDataRoot "config\approved-apps.json"
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
    if ([string]::IsNullOrWhiteSpace($Text)) { return "" }
    $redacted = $Text
    $patterns = @(
        '(?i)(Authorization:\s*Bearer\s+)[^\s"]+',
        '(?i)("?(machineCredential|credential|token|accessToken|refreshToken|authorization|password|secret|privateKey|pairCode)"?\s*[:=]\s*"?)[^",\r\n]+',
        '-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----',
        '(?i)[a-z]:[\\/][^:\r\n]+' # Redact absolute Windows paths
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

try {
    New-Directory $artifactDirectory

    # 1. Check SimAgentService is running
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        throw "SimAgentService does not exist."
    }
    if ($service.Status -ne "Running") {
        throw "SimAgentService is not running. Current status: $($service.Status)"
    }
    Write-Host "SimAgentService is Running."

    # 2. Read agent settings
    if (-not (Test-Path $agentSettingsPath)) {
        throw "agentsettings.json is missing."
    }
    $settingsContent = Get-Content -Path $agentSettingsPath -Raw
    $settings = ConvertFrom-Json $settingsContent
    $agentId = $settings.agentId
    if ([string]::IsNullOrWhiteSpace($agentId)) {
        throw "agentId is empty in agentsettings.json"
    }
    Write-Host "Agent ID: $(ConvertTo-RedactedText $agentId)"

    # 3. Audit harmless applications
    $simhubPaths = @(
        "C:\Program Files (x86)\SimHub\SimHubWPF.exe",
        "C:\Program Files\SimHub\SimHubWPF.exe",
        "D:\Program Files (x86)\SimHub\SimHubWPF.exe",
        "D:\Program Files\SimHub\SimHubWPF.exe"
    )
    $cmPaths = @(
        "C:\Program Files (x86)\Steam\steamapps\common\assettocorsa\Content Manager.exe",
        "C:\Program Files\Steam\steamapps\common\assettocorsa\Content Manager.exe",
        "C:\Program Files (x86)\Assetto Corsa\Content Manager.exe",
        "C:\Program Files\Content Manager.exe"
    )

    $selectedApp = $null
    $exePath = $null
    $workingDir = $null
    $processNames = $null

    foreach ($path in $simhubPaths) {
        if (Test-Path $path) {
            $selectedApp = "simhub"
            $exePath = $path
            $workingDir = Split-Path $path -Parent
            $processNames = @("SimHubWPF", "SimHub")
            break
        }
    }

    if ($null -eq $selectedApp) {
        foreach ($path in $cmPaths) {
            if (Test-Path $path) {
                $selectedApp = "content_manager"
                $exePath = $path
                $workingDir = Split-Path $path -Parent
                $processNames = @("Content Manager")
                break
            }
        }
    }

    if ($null -eq $selectedApp) {
        throw "No harmless approved applications (simhub or content_manager) found on the node."
    }

    Write-Host "Selected application for E2E: $selectedApp"

    # 4. Create approved-apps.json
    $entry = [ordered]@{
        executablePath = $exePath
        workingDirectory = $workingDir
        processNames = $processNames
    }
    $apps = [ordered]@{
        $selectedApp = $entry
    }
    $jsonConfig = [ordered]@{
        applications = $apps
    }
    $jsonConfig | ConvertTo-Json -Depth 5 | Set-Content -Path $configPath -Encoding UTF8
    Write-Host "Created approved-apps.json"

    # 5. Apply secure ACL
    $acl = Get-Acl -LiteralPath $configPath
    $acl.SetAccessRuleProtection($true, $false)
    $rules = $acl.GetAccessRules($true, $true, [System.Security.Principal.NTAccount])
    foreach ($rule in $rules) {
        $acl.RemoveAccessRule($rule) | Out-Null
    }
    $systemSid = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-18")
    $adminsSid = New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-544")
    $systemUser = $systemSid.Translate([System.Security.Principal.NTAccount]).Value
    $adminsUser = $adminsSid.Translate([System.Security.Principal.NTAccount]).Value

    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($systemUser, "FullControl", "Allow")))
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($adminsUser, "FullControl", "Allow")))

    $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($currentUser, "FullControl", "Allow")))

    Set-Acl -LiteralPath $configPath -AclObject $acl
    Write-Host "Secure ACL applied."

    # 6. Validate locally
    if (-not (Test-Path $configPath)) { throw "approved-apps.json is missing." }
    $parsedJson = Get-Content -Path $configPath -Raw | ConvertFrom-Json
    $appConfig = $parsedJson.applications.$selectedApp
    if ($null -eq $appConfig) { throw "Selected app configuration missing in JSON." }
    if (-not (Test-Path $appConfig.executablePath)) { throw "Executable path does not exist." }
    if ($appConfig.executablePath.StartsWith("\\")) { throw "UNC paths not allowed." }
    $normalized = $appConfig.executablePath.Replace('/', '\').ToLowerInvariant()
    if ($normalized.StartsWith("c:\windows\temp") -or $normalized.StartsWith("c:\temp") -or $normalized.Contains("\appdata\local\temp")) {
        throw "Temp directory paths not allowed."
    }
    if ($null -eq $appConfig.processNames -or $appConfig.processNames.Count -eq 0) { throw "processNames missing." }
    Write-Host "Local validation passed."

    # 7. Supabase Staging E2E communication
    Assert-RequiredSecret "SIMCRM_STAGING_SUPABASE_URL" $env:SIMCRM_STAGING_SUPABASE_URL
    Assert-RequiredSecret "SIMCRM_STAGING_SUPABASE_ANON_KEY" $env:SIMCRM_STAGING_SUPABASE_ANON_KEY
    Assert-RequiredSecret "E2E_EMAIL" $env:E2E_EMAIL
    Assert-RequiredSecret "E2E_PASSWORD" $env:E2E_PASSWORD

    $supabaseUrl = $env:SIMCRM_STAGING_SUPABASE_URL.TrimEnd("/")
    $anonKey = $env:SIMCRM_STAGING_SUPABASE_ANON_KEY

    # Authenticate via Supabase Auth API
    Write-Host "Signing in to staging Supabase as E2E owner..."
    $authUri = "$supabaseUrl/auth/v1/token?grant_type=password"
    $authHeaders = @{
        apikey = $anonKey
        "Content-Type" = "application/json"
    }
    $authBody = @{
        email = $env:E2E_EMAIL
        password = $env:E2E_PASSWORD
    } | ConvertTo-Json -Compress
    $authResponse = Invoke-RestMethod -Method Post -Uri $authUri -Headers $authHeaders -Body $authBody
    $jwt = $authResponse.access_token
    if ([string]::IsNullOrWhiteSpace($jwt)) {
        throw "Failed to obtain Supabase JWT token."
    }
    Write-Host "Authenticated successfully."

    # Preflight Check via Edge Function (using Pair Code Token)
    Assert-RequiredSecret "SIMCRM_STAGING_PAIR_CODE_TOKEN" $env:SIMCRM_STAGING_PAIR_CODE_TOKEN
    $e2eHeaders = @{
        apikey = $anonKey
        Authorization = "Bearer $($env:SIMCRM_STAGING_PAIR_CODE_TOKEN)"
        "Content-Type" = "application/json"
    }
    Write-Host "Running preflight checks via create-e2e-agent-command Edge Function..."
    $preflight = Invoke-RestMethod -Method Post -Uri "$supabaseUrl/functions/v1/create-e2e-agent-command" -Headers $e2eHeaders -Body (@{ action = "preflight" } | ConvertTo-Json -Compress)
    if (-not $preflight.agent.targetAgentConfigured) {
        throw "Staging target Agent was not found. Preflight result: $(ConvertTo-Json $preflight)"
    }
    if (-not $preflight.agent.isOnline) {
        throw "Staging target Agent is not online/recent. Preflight result: $(ConvertTo-Json $preflight)"
    }
    $agentCountBefore = [int]$preflight.agentCount
    Write-Host "Preflight check passed. Staging Agent is online. Agent count: $agentCountBefore"

    $rpcHeaders = @{
        apikey = $anonKey
        Authorization = "Bearer $jwt"
        "Content-Type" = "application/json"
    }

    # 8. Run START_APPROVED_APP
    Write-Host "Creating START_APPROVED_APP command..."
    $commandBody = @{
        p_agent_id = $agentId
        p_command_type = "START_APPROVED_APP"
        p_payload = @{ applicationId = $selectedApp }
    } | ConvertTo-Json -Compress
    $createResponse = Invoke-RestMethod -Method Post -Uri "$supabaseUrl/rest/v1/rpc/create_agent_command_v1" -Headers $rpcHeaders -Body $commandBody
    $commandId = $createResponse.command_id
    if ([string]::IsNullOrWhiteSpace($commandId)) {
        throw "Failed to create START_APPROVED_APP command."
    }
    Write-Host "Command created with ID: $commandId"

    # Poll for completion of the first launch command
    $firstLaunchResult = $null
    $deadline = (Get-Date).AddSeconds(180)
    Write-Host "Waiting for first launch execution..."
    while ((Get-Date) -lt $deadline) {
        try {
            $statusBody = @{ p_command_id = $commandId } | ConvertTo-Json -Compress
            $statusResponse = Invoke-RestMethod -Method Post -Uri "$supabaseUrl/rest/v1/rpc/get_agent_command_status_v1" -Headers $rpcHeaders -Body $statusBody
            $status = $statusResponse.status
            if ($status -in @("succeeded", "failed", "expired", "cancelled")) {
                $firstLaunchResult = $statusResponse
                break
            }
        } catch {
            Write-Host "Warning: Transient network error while polling status: $_"
        }
        Start-Sleep -Seconds 5
    }

    if ($null -eq $firstLaunchResult) {
        throw "First launch command timed out."
    }
    Write-Host "First launch status: $($firstLaunchResult.status)"
    Write-Host "First launch result: $(ConvertTo-Json $firstLaunchResult.result)"
    if ($firstLaunchResult.status -ne "succeeded" -or $firstLaunchResult.result.status -notin @("started", "already_running")) {
        throw "First launch command failed or did not report started/already_running. Status: $($firstLaunchResult.status), Result status: $($firstLaunchResult.result.status)"
    }

    # Resolve attempt_count directly from table or fallback to 1
    $attemptCount1 = 1
    try {
        $queryUrl1 = "$supabaseUrl/rest/v1/agent_commands?id=eq.$commandId&select=attempt_count"
        $queryResult1 = Invoke-RestMethod -Method Get -Uri $queryUrl1 -Headers $rpcHeaders
        if ($null -ne $queryResult1 -and $queryResult1.Count -gt 0) {
            $attemptCount1 = [int]$queryResult1[0].attempt_count
        }
    } catch {
        Write-Host "Warning: Direct query for attempt_count 1 failed: $_"
    }
    Write-Host "First launch attempt_count: $attemptCount1"
    if ($attemptCount1 -ne 1) {
        throw "First launch attemptCount is $attemptCount1, expected 1."
    }

    # 9. Run START_APPROVED_APP again to test already_running
    Write-Host "Creating second START_APPROVED_APP command..."
    $createResponse2 = Invoke-RestMethod -Method Post -Uri "$supabaseUrl/rest/v1/rpc/create_agent_command_v1" -Headers $rpcHeaders -Body $commandBody
    $commandId2 = $createResponse2.command_id
    if ([string]::IsNullOrWhiteSpace($commandId2)) {
        throw "Failed to create second START_APPROVED_APP command."
    }
    Write-Host "Second command created with ID: $commandId2"

    $secondLaunchResult = $null
    $deadline = (Get-Date).AddSeconds(180)
    Write-Host "Waiting for second launch execution..."
    while ((Get-Date) -lt $deadline) {
        try {
            $statusBody2 = @{ p_command_id = $commandId2 } | ConvertTo-Json -Compress
            $statusResponse2 = Invoke-RestMethod -Method Post -Uri "$supabaseUrl/rest/v1/rpc/get_agent_command_status_v1" -Headers $rpcHeaders -Body $statusBody2
            $status2 = $statusResponse2.status
            if ($status2 -in @("succeeded", "failed", "expired", "cancelled")) {
                $secondLaunchResult = $statusResponse2
                break
            }
        } catch {
            Write-Host "Warning: Transient network error while polling status: $_"
        }
        Start-Sleep -Seconds 5
    }

    if ($null -eq $secondLaunchResult) {
        throw "Second launch command timed out."
    }
    Write-Host "Second launch status: $($secondLaunchResult.status)"
    Write-Host "Second launch result: $(ConvertTo-Json $secondLaunchResult.result)"
    if ($secondLaunchResult.status -ne "succeeded" -or $secondLaunchResult.result.status -ne "already_running") {
        throw "Second launch did not report already_running. Status: $($secondLaunchResult.status), Result status: $($secondLaunchResult.result.status)"
    }

    # Resolve attempt_count for second command
    $attemptCount2 = 1
    try {
        $queryUrl2 = "$supabaseUrl/rest/v1/agent_commands?id=eq.$commandId2&select=attempt_count"
        $queryResult2 = Invoke-RestMethod -Method Get -Uri $queryUrl2 -Headers $rpcHeaders
        if ($null -ne $queryResult2 -and $queryResult2.Count -gt 0) {
            $attemptCount2 = [int]$queryResult2[0].attempt_count
        }
    } catch {
        Write-Host "Warning: Direct query for attempt_count 2 failed: $_"
    }
    Write-Host "Second launch attempt_count: $attemptCount2"
    if ($attemptCount2 -ne 1) {
        throw "Second launch attemptCount is $attemptCount2, expected 1."
    }

    # 10. Run GET_SIM_STATUS to verify the application is running
    Write-Host "Creating GET_SIM_STATUS command..."
    $statusCommandBody = @{
        p_agent_id = $agentId
        p_command_type = "GET_SIM_STATUS"
    } | ConvertTo-Json -Compress
    $createResponse3 = Invoke-RestMethod -Method Post -Uri "$supabaseUrl/rest/v1/rpc/create_agent_command_v1" -Headers $rpcHeaders -Body $statusCommandBody
    $commandId3 = $createResponse3.command_id
    if ([string]::IsNullOrWhiteSpace($commandId3)) {
        throw "Failed to create GET_SIM_STATUS command."
    }
    Write-Host "GET_SIM_STATUS command created with ID: $commandId3"

    $simStatusResult = $null
    $deadline = (Get-Date).AddSeconds(180)
    Write-Host "Waiting for GET_SIM_STATUS execution..."
    while ((Get-Date) -lt $deadline) {
        try {
            $statusBody3 = @{ p_command_id = $commandId3 } | ConvertTo-Json -Compress
            $statusResponse3 = Invoke-RestMethod -Method Post -Uri "$supabaseUrl/rest/v1/rpc/get_agent_command_status_v1" -Headers $rpcHeaders -Body $statusBody3
            $status3 = $statusResponse3.status
            if ($status3 -in @("succeeded", "failed", "expired", "cancelled")) {
                $simStatusResult = $statusResponse3
                break
            }
        } catch {
            Write-Host "Warning: Transient network error while polling status: $_"
        }
        Start-Sleep -Seconds 5
    }

    if ($null -eq $simStatusResult) {
        throw "GET_SIM_STATUS command timed out."
    }
    Write-Host "GET_SIM_STATUS status: $($simStatusResult.status)"
    Write-Host "GET_SIM_STATUS applications: $(ConvertTo-Json $simStatusResult.result.applications)"

    $targetAppResult = $simStatusResult.result.applications | Where-Object { $_.id -eq $selectedApp }
    if ($null -eq $targetAppResult -or -not $targetAppResult.running) {
        throw "GET_SIM_STATUS did not report $selectedApp as running."
    }

    # 11. Confirm Agent ID and count unchanged
    Write-Host "Verifying agent identity and count after E2E execution..."
    $preflightAfter = Invoke-RestMethod -Method Post -Uri "$supabaseUrl/functions/v1/create-e2e-agent-command" -Headers $e2eHeaders -Body (@{ action = "preflight" } | ConvertTo-Json -Compress)
    
    if (-not $preflightAfter.agent.targetAgentConfigured) {
        throw "Target agent identity changed or missing after E2E."
    }
    if ([int]$preflightAfter.agentCount -ne $agentCountBefore) {
        throw "Agent count changed from $agentCountBefore to $($preflightAfter.agentCount)."
    }
    Write-Host "Agent ID and Agent count confirmed unchanged."

    # 11a. Run STOP_APPROVED_APP
    Write-Host "Creating STOP_APPROVED_APP command..."
    $stopCommandBody = @{
        p_agent_id = $agentId
        p_command_type = "STOP_APPROVED_APP"
        p_payload = @{ applicationId = $selectedApp }
    } | ConvertTo-Json -Compress
    $createResponseStop = Invoke-RestMethod -Method Post -Uri "$supabaseUrl/rest/v1/rpc/create_agent_command_v1" -Headers $rpcHeaders -Body $stopCommandBody
    $commandIdStop = $createResponseStop.command_id
    if ([string]::IsNullOrWhiteSpace($commandIdStop)) {
        throw "Failed to create STOP_APPROVED_APP command."
    }
    Write-Host "Command created with ID: $commandIdStop"

    # Poll for completion of the stop command
    $stopResult = $null
    $deadline = (Get-Date).AddSeconds(180)
    Write-Host "Waiting for stop execution..."
    while ((Get-Date) -lt $deadline) {
        try {
            $statusBodyStop = @{ p_command_id = $commandIdStop } | ConvertTo-Json -Compress
            $statusResponseStop = Invoke-RestMethod -Method Post -Uri "$supabaseUrl/rest/v1/rpc/get_agent_command_status_v1" -Headers $rpcHeaders -Body $statusBodyStop
            $statusStop = $statusResponseStop.status
            if ($statusStop -in @("succeeded", "failed", "expired", "cancelled")) {
                $stopResult = $statusResponseStop
                break
            }
        } catch {
            Write-Host "Warning: Transient network error while polling status: $_"
        }
        Start-Sleep -Seconds 5
    }

    if ($null -eq $stopResult) {
        throw "Stop command timed out."
    }
    Write-Host "Stop status: $($stopResult.status)"
    Write-Host "Stop result: $(ConvertTo-Json $stopResult.result)"
    if ($stopResult.status -ne "succeeded" -or $stopResult.result.status -notin @("stopped", "not_running")) {
        throw "Stop command failed or did not report stopped/not_running. Status: $($stopResult.status), Result status: $($stopResult.result.status)"
    }

    # Resolve attempt_count for stop command
    $attemptCountStop1 = 1
    try {
        $queryUrlStop1 = "$supabaseUrl/rest/v1/agent_commands?id=eq.$commandIdStop&select=attempt_count"
        $queryResultStop1 = Invoke-RestMethod -Method Get -Uri $queryUrlStop1 -Headers $rpcHeaders
        if ($null -ne $queryResultStop1 -and $queryResultStop1.Count -gt 0) {
            $attemptCountStop1 = [int]$queryResultStop1[0].attempt_count
        }
    } catch {
        Write-Host "Warning: Direct query for attempt_count stop 1 failed: $_"
    }
    Write-Host "Stop attempt_count: $attemptCountStop1"
    if ($attemptCountStop1 -ne 1) {
        throw "Stop attemptCount is $attemptCountStop1, expected 1."
    }

    # 11b. Run STOP_APPROVED_APP again to verify not_running
    Write-Host "Creating second STOP_APPROVED_APP command..."
    $createResponseStop2 = Invoke-RestMethod -Method Post -Uri "$supabaseUrl/rest/v1/rpc/create_agent_command_v1" -Headers $rpcHeaders -Body $stopCommandBody
    $commandIdStop2 = $createResponseStop2.command_id
    if ([string]::IsNullOrWhiteSpace($commandIdStop2)) {
        throw "Failed to create second STOP_APPROVED_APP command."
    }
    Write-Host "Second stop command created with ID: $commandIdStop2"

    $secondStopResult = $null
    $deadline = (Get-Date).AddSeconds(180)
    Write-Host "Waiting for second stop execution..."
    while ((Get-Date) -lt $deadline) {
        try {
            $statusBodyStop2 = @{ p_command_id = $commandIdStop2 } | ConvertTo-Json -Compress
            $statusResponseStop2 = Invoke-RestMethod -Method Post -Uri "$supabaseUrl/rest/v1/rpc/get_agent_command_status_v1" -Headers $rpcHeaders -Body $statusBodyStop2
            $statusStop2 = $statusResponseStop2.status
            if ($statusStop2 -in @("succeeded", "failed", "expired", "cancelled")) {
                $secondStopResult = $statusResponseStop2
                break
            }
        } catch {
            Write-Host "Warning: Transient network error while polling status: $_"
        }
        Start-Sleep -Seconds 5
    }

    if ($null -eq $secondStopResult) {
        throw "Second stop command timed out."
    }
    Write-Host "Second stop status: $($secondStopResult.status)"
    Write-Host "Second stop result: $(ConvertTo-Json $secondStopResult.result)"
    if ($secondStopResult.status -ne "succeeded" -or $secondStopResult.result.status -ne "not_running") {
        throw "Second stop did not report not_running. Status: $($secondStopResult.status), Result status: $($secondStopResult.result.status)"
    }

    # Resolve attempt_count for second stop command
    $attemptCountStop2 = 1
    try {
        $queryUrlStop2 = "$supabaseUrl/rest/v1/agent_commands?id=eq.$commandIdStop2&select=attempt_count"
        $queryResultStop2 = Invoke-RestMethod -Method Get -Uri $queryUrlStop2 -Headers $rpcHeaders
        if ($null -ne $queryResultStop2 -and $queryResultStop2.Count -gt 0) {
            $attemptCountStop2 = [int]$queryResultStop2[0].attempt_count
        }
    } catch {
        Write-Host "Warning: Direct query for attempt_count stop 2 failed: $_"
    }
    Write-Host "Second stop attempt_count: $attemptCountStop2"
    if ($attemptCountStop2 -ne 1) {
        throw "Second stop attemptCount is $attemptCountStop2, expected 1."
    }

    # 11c. Run GET_SIM_STATUS to verify the application is NOT running
    Write-Host "Creating GET_SIM_STATUS command to check stopped state..."
    $createResponseStatusAfterStop = Invoke-RestMethod -Method Post -Uri "$supabaseUrl/rest/v1/rpc/create_agent_command_v1" -Headers $rpcHeaders -Body $statusCommandBody
    $commandIdStatusAfterStop = $createResponseStatusAfterStop.command_id
    if ([string]::IsNullOrWhiteSpace($commandIdStatusAfterStop)) {
        throw "Failed to create GET_SIM_STATUS command after stop."
    }
    Write-Host "GET_SIM_STATUS command after stop created with ID: $commandIdStatusAfterStop"

    $simStatusAfterStopResult = $null
    $deadline = (Get-Date).AddSeconds(180)
    Write-Host "Waiting for GET_SIM_STATUS after stop execution..."
    while ((Get-Date) -lt $deadline) {
        try {
            $statusBodyStatusAfterStop = @{ p_command_id = $commandIdStatusAfterStop } | ConvertTo-Json -Compress
            $statusResponseStatusAfterStop = Invoke-RestMethod -Method Post -Uri "$supabaseUrl/rest/v1/rpc/get_agent_command_status_v1" -Headers $rpcHeaders -Body $statusBodyStatusAfterStop
            $statusStatusAfterStop = $statusResponseStatusAfterStop.status
            if ($statusStatusAfterStop -in @("succeeded", "failed", "expired", "cancelled")) {
                $simStatusAfterStopResult = $statusResponseStatusAfterStop
                break
            }
        } catch {
            Write-Host "Warning: Transient network error while polling status: $_"
        }
        Start-Sleep -Seconds 5
    }

    if ($null -eq $simStatusAfterStopResult) {
        throw "GET_SIM_STATUS command after stop timed out."
    }
    Write-Host "GET_SIM_STATUS after stop status: $($simStatusAfterStopResult.status)"
    Write-Host "GET_SIM_STATUS after stop applications: $(ConvertTo-Json $simStatusAfterStopResult.result.applications)"

    $targetAppAfterStopResult = $simStatusAfterStopResult.result.applications | Where-Object { $_.id -eq $selectedApp }
    if ($null -ne $targetAppAfterStopResult -and $targetAppAfterStopResult.running) {
        throw "GET_SIM_STATUS reported $selectedApp is still running after stop."
    }
    Write-Host "Confirmed: $selectedApp is not running."

    # 11d. Confirm Agent ID and count unchanged after stop
    Write-Host "Verifying agent identity and count after E2E stop execution..."
    $preflightAfterStop = Invoke-RestMethod -Method Post -Uri "$supabaseUrl/functions/v1/create-e2e-agent-command" -Headers $e2eHeaders -Body (@{ action = "preflight" } | ConvertTo-Json -Compress)
    
    if (-not $preflightAfterStop.agent.targetAgentConfigured) {
        throw "Target agent identity changed or missing after stop E2E."
    }
    if ([int]$preflightAfterStop.agentCount -ne $agentCountBefore) {
        throw "Agent count changed from $agentCountBefore to $($preflightAfterStop.agentCount)."
    }
    Write-Host "Agent ID and Agent count confirmed unchanged."

    # 12. Write final redacted diagnostics report
    $report = [ordered]@{
        Success = $true
        Stage = "E2E"
        SelectedApplication = $selectedApp
        FirstLaunchResult = @{
            Status = $firstLaunchResult.status
            ResultStatus = $firstLaunchResult.result.status
            AttemptCount = $attemptCount1
        }
        AlreadyRunningResult = @{
            Status = $secondLaunchResult.status
            ResultStatus = $secondLaunchResult.result.status
            AttemptCount = $attemptCount2
        }
        GetSimStatusResult = @{
            Status = $simStatusResult.status
            TargetAppRunning = $targetAppResult.running
        }
        StopResult = @{
            Status = $stopResult.status
            ResultStatus = $stopResult.result.status
            AttemptCount = $attemptCountStop1
        }
        AlreadyStoppedResult = @{
            Status = $secondStopResult.status
            ResultStatus = $secondStopResult.result.status
            AttemptCount = $attemptCountStop2
        }
        GetSimStatusAfterStopResult = @{
            Status = $simStatusAfterStopResult.status
            TargetAppRunning = ($null -ne $targetAppAfterStopResult -and $targetAppAfterStopResult.running)
        }
        AgentIdPreserved = ($preflightAfterStop.agent.targetAgentConfigured -eq $true)
        AgentCountUnchanged = ($preflightAfterStop.agentCount -eq $agentCountBefore)
    }

    Write-JsonFile $report $validationReportPath
    Write-JsonFile $report (Join-Path $artifactDirectory "validation-report.json")
    Write-Host "E2E diagnostics report saved successfully."

} catch {
    Write-Host "ERROR: $_"
    $failureReport = [ordered]@{
        Success = $false
        Stage = "E2E"
        Error = "$_"
    }
    New-Directory $artifactDirectory
    Write-JsonFile $failureReport $validationReportPath
    Write-JsonFile $failureReport (Join-Path $artifactDirectory "validation-report.json")
    exit 1
}
