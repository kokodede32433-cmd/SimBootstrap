using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine;

public class PackageRegistry : IPackageRegistry
{
    private readonly List<PackageManifest> _manifests = new();
    private readonly Dictionary<string, string> _installed = new();
    private readonly IPackageManifestLoader _loader;

    public PackageRegistry(IPackageManifestLoader loader)
    {
        _loader = loader;
    }

    public async Task DiscoverManifestsAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        var manifests = await _loader.LoadAllAsync(directoryPath, cancellationToken);
        _manifests.Clear();
        _manifests.AddRange(manifests);
    }

    public IEnumerable<PackageManifest> GetAvailablePackages() => _manifests;

    public PackageManifest? GetPackageById(string packageId) =>
        _manifests.FirstOrDefault(p => p.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase));

    public Task RegisterInstallationAsync(string packageId, string version, CancellationToken cancellationToken = default)
    {
        _installed[packageId] = version;
        return Task.CompletedTask;
    }

    public Task<bool> IsPackageInstalledAsync(string packageId, string version, CancellationToken cancellationToken = default)
    {
        var found = _installed.TryGetValue(packageId, out var installedVersion) && installedVersion == version;
        return Task.FromResult(found);
    }
}
