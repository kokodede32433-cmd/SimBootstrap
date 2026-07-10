using System;
using System.Collections.Generic;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine;

public class DependencyCycleDetector : IDependencyCycleDetector
{
    public bool HasCycle(DependencyGraphNode rootNode, out List<string> cyclePath)
    {
        cyclePath = new List<string>();
        var visited = new Dictionary<string, NodeState>(StringComparer.OrdinalIgnoreCase);
        var pathStack = new List<string>();

        return DetectCycleDfs(rootNode, visited, pathStack, cyclePath);
    }

    private enum NodeState
    {
        Visiting, // Gray
        Visited   // Black
    }

    private bool DetectCycleDfs(
        DependencyGraphNode node,
        Dictionary<string, NodeState> visited,
        List<string> pathStack,
        List<string> cyclePath)
    {
        var id = node.PackageId;
        
        if (visited.TryGetValue(id, out var state))
        {
            if (state == NodeState.Visiting)
            {
                int index = pathStack.IndexOf(id);
                if (index >= 0)
                {
                    for (int i = index; i < pathStack.Count; i++)
                    {
                        cyclePath.Add(pathStack[i]);
                    }
                    cyclePath.Add(id);
                }
                return true;
            }
            return false;
        }

        visited[id] = NodeState.Visiting;
        pathStack.Add(id);

        foreach (var dep in node.Dependencies)
        {
            if (DetectCycleDfs(dep, visited, pathStack, cyclePath))
            {
                return true;
            }
        }

        pathStack.RemoveAt(pathStack.Count - 1);
        visited[id] = NodeState.Visited;

        return false;
    }
}
