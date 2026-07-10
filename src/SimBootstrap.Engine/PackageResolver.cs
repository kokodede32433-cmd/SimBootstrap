using System;
using System.Collections.Generic;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine;

public class PackageResolver : IPackageResolver
{
    private readonly IDependencyGraphBuilder _graphBuilder;
    private readonly IDependencyCycleDetector _cycleDetector;

    public PackageResolver(IDependencyGraphBuilder graphBuilder, IDependencyCycleDetector cycleDetector)
    {
        _graphBuilder = graphBuilder;
        _cycleDetector = cycleDetector;
    }

    public DependencyResolutionResult ResolveDependencies(string rootPackageId)
    {
        var node = _graphBuilder.BuildGraph(rootPackageId, out var missing);
        if (missing.Count > 0)
        {
            return new DependencyResolutionResult(
                DependencyResolutionStatus.MissingDependency,
                new List<string>(),
                MissingPackageId: missing[0],
                ErrorMessage: $"Missing dependency manifest for package: '{missing[0]}'"
            );
        }

        if (_cycleDetector.HasCycle(node, out var cyclePath))
        {
            var cycleStr = string.Join(" -> ", cyclePath);
            return new DependencyResolutionResult(
                DependencyResolutionStatus.CycleDetected,
                new List<string>(),
                CyclePath: cyclePath,
                ErrorMessage: $"Circular dependency detected: {cycleStr}"
            );
        }

        var order = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        TopologicalSort(node, visited, order);

        return new DependencyResolutionResult(
            DependencyResolutionStatus.Success,
            order
        );
    }

    private void TopologicalSort(DependencyGraphNode node, HashSet<string> visited, List<string> order)
    {
        if (visited.Contains(node.PackageId))
        {
            return;
        }

        visited.Add(node.PackageId);

        foreach (var dep in node.Dependencies)
        {
            TopologicalSort(dep, visited, order);
        }

        order.Add(node.PackageId);
    }
}
