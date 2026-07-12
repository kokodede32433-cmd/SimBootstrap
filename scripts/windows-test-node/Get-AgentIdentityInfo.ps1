$ErrorActionPreference = "Stop"

$programDataRoot = "C:\ProgramData\SimBootstrap"
$programFilesRoot = "C:\Program Files\SimBootstrap"
$agentSettingsPath = Join-Path $programDataRoot "config\agentsettings.json"
$configPath = Join-Path $programDataRoot "config\approved-apps.json"
$agentExePath = Join-Path $programFilesRoot "Agent\SimAgent.Service.exe"

$agentId = "Unknown"
if (Test-Path $agentSettingsPath) {
    try {
        $settings = Get-Content -Path $agentSettingsPath -Raw | ConvertFrom-Json
        $agentId = $settings.agentId
    } catch {
        Write-Warning "Failed to parse agentsettings.json: $_"
    }
}

$agentVersion = "Unknown"
if (Test-Path $agentExePath) {
    try {
        $agentVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($agentExePath).FileVersion
    } catch {
        Write-Warning "Failed to read file version from SimAgent.Service.exe: $_"
    }
}

$serviceStatus = "Missing"
$service = Get-Service -Name "SimAgentService" -ErrorAction SilentlyContinue
if ($null -ne $service) {
    $serviceStatus = $service.Status.ToString()
}

$approvedAppIds = @()
if (Test-Path $configPath) {
    try {
        $config = Get-Content -Path $configPath -Raw | ConvertFrom-Json
        if ($null -ne $config.applications) {
            $approvedAppIds = $config.applications.psobject.properties.name
        }
    } catch {
        Write-Warning "Failed to parse approved-apps.json: $_"
    }
}

# Determine if an interactive user session is present
$hasInteractive = $false
try {
    # Check query user output
    $queryUser = query user 2>$null
    if ($null -ne $queryUser) {
        # If there's an active session or a console session
        $hasActive = ($queryUser -match '(?i)active') -or ($queryUser -match '(?i)console') -or ($queryUser -match '(?i)>')
        if ($hasActive) {
            $hasInteractive = $true
        }
    }
} catch {
    # Fallback to checking explorer process
}

if (-not $hasInteractive) {
    $explorer = Get-Process explorer -ErrorAction SilentlyContinue
    if ($null -ne $explorer) {
        $hasInteractive = $true
    }
}

$report = [ordered]@{
    AgentId = $agentId
    MachineId = $env:COMPUTERNAME
    WindowsHostname = $env:COMPUTERNAME
    SimAgentServiceStatus = $serviceStatus
    AgentVersion = $agentVersion
    ApprovedApplicationIds = $approvedAppIds
    InteractiveUserSessionPresent = $hasInteractive
}

$report | ConvertTo-Json | Write-Output
