using System;
using System.Collections.Generic;

namespace SimBootstrap.Contracts;

public enum ProvisioningStatus
{
    NotStarted,
    Running,
    Completed,
    Failed,
    Skipped
}

public record ProvisioningLogEntry(DateTime Timestamp, string Level, string Message);

public class ProvisioningStepResult
{
    public ProvisioningStatus Status { get; set; } = ProvisioningStatus.NotStarted;
    public string StepName { get; set; } = string.Empty;
    public List<string> Logs { get; } = new();
    public string? ErrorMessage { get; set; }

    public static ProvisioningStepResult Success(string stepName, List<string>? logs = null)
    {
        var result = new ProvisioningStepResult
        {
            Status = ProvisioningStatus.Completed,
            StepName = stepName
        };
        if (logs != null)
        {
            result.Logs.AddRange(logs);
        }
        return result;
    }

    public static ProvisioningStepResult Skip(string stepName, string reason, List<string>? logs = null)
    {
        var result = new ProvisioningStepResult
        {
            Status = ProvisioningStatus.Skipped,
            StepName = stepName,
            ErrorMessage = reason
        };
        if (logs != null)
        {
            result.Logs.AddRange(logs);
        }
        return result;
    }

    public static ProvisioningStepResult Failure(string stepName, string errorMessage, List<string>? logs = null)
    {
        var result = new ProvisioningStepResult
        {
            Status = ProvisioningStatus.Failed,
            StepName = stepName,
            ErrorMessage = errorMessage
        };
        if (logs != null)
        {
            result.Logs.AddRange(logs);
        }
        return result;
    }
}

public class ProvisioningResult
{
    public bool Success { get; set; }
    public List<ProvisioningStepResult> StepResults { get; } = new();
    public List<ProvisioningLogEntry> EngineLogs { get; } = new();
}
