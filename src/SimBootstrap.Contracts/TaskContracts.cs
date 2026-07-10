using System;
using System.Collections.Generic;

namespace SimBootstrap.Contracts;

public enum BootstrapTaskType
{
    InstallPackage,
    RepairPackage,
    UpdatePackage,
    RunDiagnostics,
    ProvisionSystem
}

public enum BootstrapTaskStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Aborted
}

public record BootstrapTaskLogEntry(DateTime Timestamp, string Message);

public class BootstrapTask
{
    public string TaskId { get; set; } = string.Empty;
    public BootstrapTaskType Type { get; set; }
    public string? TargetPackageId { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();
}

public class BootstrapTaskResult
{
    public string TaskId { get; set; } = string.Empty;
    public BootstrapTaskStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public List<BootstrapTaskLogEntry> Logs { get; } = new();
    public DateTime CompletedAtUtc { get; set; }
}
