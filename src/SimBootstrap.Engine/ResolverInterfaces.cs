using System.Collections.Generic;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine;

public interface IPackageResolver
{
    DependencyResolutionResult ResolveDependencies(string rootPackageId);
}

public interface IDependencyGraphBuilder
{
    DependencyGraphNode BuildGraph(string rootPackageId, out List<string> missingPackageIds);
}

public interface IDependencyCycleDetector
{
    bool HasCycle(DependencyGraphNode rootNode, out List<string> cyclePath);
}
