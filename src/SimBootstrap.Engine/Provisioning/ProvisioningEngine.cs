using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;
using SimBootstrap.Engine.Provisioning.Steps;

namespace SimBootstrap.Engine.Provisioning;

public class ProvisioningEngine
{
    private readonly ICommandRunner _commandRunner;
    private readonly IWindowsCapabilityChecker _capabilityChecker;
    private readonly List<IProvisioningStep> _steps;
    private readonly Func<bool> _isWindowsProvider;

    public ProvisioningEngine(
        ICommandRunner commandRunner,
        IWindowsCapabilityChecker capabilityChecker,
        Func<bool>? isWindowsProvider = null)
    {
        _commandRunner = commandRunner;
        _capabilityChecker = capabilityChecker;
        _isWindowsProvider = isWindowsProvider ?? (() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
        _steps = new List<IProvisioningStep>
        {
            new EnsureGitInstalled(),
            new EnsureDotNet9SdkInstalled(),
            new EnsureOpenSshServerInstalled(),
            new EnsureSshdRunning(),
            new EnsureSshdAutostart(),
            new EnsureFirewallRuleForSsh(),
            new EnsureAuthorizedKeys(),
            new EnsureNoSleepPowerPlan()
        };
    }

    // Constructor that accepts custom steps for testing flexibility
    public ProvisioningEngine(
        ICommandRunner commandRunner,
        IWindowsCapabilityChecker capabilityChecker,
        List<IProvisioningStep> steps,
        Func<bool>? isWindowsProvider = null)
    {
        _commandRunner = commandRunner;
        _capabilityChecker = capabilityChecker;
        _steps = steps;
        _isWindowsProvider = isWindowsProvider ?? (() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
    }

    public async Task<ProvisioningResult> RunProvisioningAsync(ProvisioningConfig config, bool dryRun, CancellationToken cancellationToken = default)
    {
        var result = new ProvisioningResult();
        
        void LogInfo(string msg) => result.EngineLogs.Add(new ProvisioningLogEntry(DateTime.UtcNow, "INFO", msg));
        void LogError(string msg) => result.EngineLogs.Add(new ProvisioningLogEntry(DateTime.UtcNow, "ERROR", msg));
        void LogWarning(string msg) => result.EngineLogs.Add(new ProvisioningLogEntry(DateTime.UtcNow, "WARNING", msg));

        LogInfo($"Starting Windows Provisioning Bootstrap orchestration (Dry-run: {dryRun})...");

        // 1. Validate Config
        try
        {
            config.Validate();
            LogInfo("Configuration validated successfully.");
        }
        catch (Exception ex)
        {
            LogError($"Configuration validation failed: {ex.Message}");
            result.Success = false;
            return result;
        }

        // 2. Query System Capabilities
        LogInfo("Checking system capabilities...");
        WindowsCapabilities caps;
        try
        {
            caps = await _capabilityChecker.CheckCapabilitiesAsync(cancellationToken);
            LogInfo($"System: {caps.WindowsVersion}");
            LogInfo($"PowerShell: {caps.PowerShellVersion}");
            LogInfo($"WinGet Available: {caps.IsWinGetAvailable}");
            LogInfo($"Administrator: {caps.IsAdmin}");
        }
        catch (Exception ex)
        {
            LogError($"Failed to detect system capabilities: {ex.Message}");
            result.Success = false;
            return result;
        }

        var isWindows = _isWindowsProvider();

        // 3. Enforce OS and Privilege Checks for real execution
        if (!dryRun)
        {
            if (!isWindows)
            {
                LogError("Error: Real provisioning changes can only be applied on a Windows operating system.");
                result.Success = false;
                return result;
            }

            if (!caps.IsAdmin)
            {
                LogError("Administrator privileges are required for --apply.");
                result.Success = false;
                return result;
            }
        }
        else
        {
            if (!isWindows)
            {
                LogWarning("[Dry-run Mode] Running on a non-Windows platform. Simulating execution steps.");
            }
            else if (!caps.IsAdmin)
            {
                LogWarning("[Dry-run Mode] Running without administrator privileges. Simulating execution steps.");
            }
        }

        var context = new ProvisioningContext(_commandRunner, config, caps);
        var allStepsPassed = true;

        // 4. Execute Steps In Order
        foreach (var step in _steps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            LogInfo($"Evaluating step '{step.Name}'...");

            bool shouldRun;
            try
            {
                shouldRun = await step.ShouldRunAsync(context, cancellationToken);
            }
            catch (Exception ex)
            {
                LogError($"Step '{step.Name}' encountered error during ShouldRun check: {ex.Message}");
                var failResult = ProvisioningStepResult.Failure(step.Name, $"ShouldRun error: {ex.Message}");
                result.StepResults.Add(failResult);
                
                if (step.IsCritical)
                {
                    LogError($"Critical step '{step.Name}' failed evaluation. Aborting subsequent steps.");
                    allStepsPassed = false;
                    break;
                }
                continue;
            }

            if (!shouldRun)
            {
                LogInfo($"Step '{step.Name}' is skipped based on configuration or state.");
                result.StepResults.Add(ProvisioningStepResult.Skip(step.Name, "Skipped by configuration/state"));
                continue;
            }

            LogInfo($"Executing step '{step.Name}': {step.Description}");
            ProvisioningStepResult stepResult;
            try
            {
                stepResult = await step.ExecuteAsync(context, dryRun, cancellationToken);
            }
            catch (Exception ex)
            {
                LogError($"Step '{step.Name}' threw an exception during execution: {ex.Message}");
                stepResult = ProvisioningStepResult.Failure(step.Name, $"Execution exception: {ex.Message}");
            }

            result.StepResults.Add(stepResult);

            if (stepResult.Status == ProvisioningStatus.Failed)
            {
                LogError($"Step '{step.Name}' failed: {stepResult.ErrorMessage}");
                if (step.IsCritical)
                {
                    LogError($"Critical step '{step.Name}' failed. Aborting provisioning flow.");
                    allStepsPassed = false;
                    break;
                }
            }
            else
            {
                LogInfo($"Step '{step.Name}' completed with status: {stepResult.Status}");
            }
        }

        result.Success = allStepsPassed;
        LogInfo($"Provisioning flow complete. Final outcome: {(result.Success ? "SUCCESS" : "FAILED")}");
        return result;
    }
}
