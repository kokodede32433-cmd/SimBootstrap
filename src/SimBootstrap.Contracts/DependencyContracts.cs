using System.Collections.Generic;

namespace SimBootstrap.Contracts;

public enum DependencyResolutionStatus
{
    Success,
    CycleDetected,
    MissingDependency,
    Failed
}

public record PackageDependency(string PackageId);

public record DependencyResolutionResult(
    DependencyResolutionStatus Status,
    List<string> OrderedPackageIds,
    List<string>? CyclePath = null,
    string? MissingPackageId = null,
    string? ErrorMessage = null
);

public class DependencyGraphNode
{
    public string PackageId { get; }
    public List<DependencyGraphNode> Dependencies { get; } = new();

    public DependencyGraphNode(string packageId)
    {
        PackageId = packageId;
    }
}

public record DependencyInstallPlan(
    List<string> OrderedPackageIds
);
