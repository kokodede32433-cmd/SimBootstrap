using System;
using System.Collections.Generic;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine;

public class DependencyGraphBuilder : IDependencyGraphBuilder
{
    private readonly PackageRegistry _registry;

    public DependencyGraphBuilder(PackageRegistry registry)
    {
        _registry = registry;
    }

    public DependencyGraphNode BuildGraph(string rootPackageId, out List<string> missingPackageIds)
    {
        missingPackageIds = new List<string>();
        var visited = new Dictionary<string, DependencyGraphNode>(StringComparer.OrdinalIgnoreCase);
        return BuildGraphInternal(rootPackageId, visited, missingPackageIds);
    }

    private DependencyGraphNode BuildGraphInternal(
        string packageId,
        Dictionary<string, DependencyGraphNode> visited,
        List<string> missingPackageIds)
    {
        if (visited.TryGetValue(packageId, out var existingNode))
        {
            return existingNode;
        }

        var node = new DependencyGraphNode(packageId);
        visited[packageId] = node;

        var manifest = _registry.GetPackageById(packageId);
        if (manifest == null)
        {
            if (!missingPackageIds.Contains(packageId))
            {
                missingPackageIds.Add(packageId);
            }
            return node;
        }

        if (manifest.Dependencies != null)
        {
            foreach (var depId in manifest.Dependencies)
            {
                var depNode = BuildGraphInternal(depId, visited, missingPackageIds);
                node.Dependencies.Add(depNode);
            }
        }

        return node;
    }
}
