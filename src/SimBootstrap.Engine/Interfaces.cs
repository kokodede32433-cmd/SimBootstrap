using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine;

public interface IBootstrapEngine
{
    Task<bool> RunBootstrapAsync(CancellationToken cancellationToken = default);
}

public interface IPackageInstaller
{
    Task<bool> InstallAsync(PackageManifest manifest, CancellationToken cancellationToken = default);
}

public interface IPackageDownloader
{
    Task<string> DownloadAsync(string url, string destinationPath, CancellationToken cancellationToken = default);
}

public interface IPackageVerifier
{
    Task<bool> VerifyAsync(PackageManifest manifest, CancellationToken cancellationToken = default);
}

public interface IPackageRepair
{
    Task<bool> RepairAsync(PackageManifest manifest, CancellationToken cancellationToken = default);
}

public interface IPackageRegistry
{
    Task RegisterInstallationAsync(string packageId, string version, CancellationToken cancellationToken = default);
    Task<bool> IsPackageInstalledAsync(string packageId, string version, CancellationToken cancellationToken = default);
}

public interface IPackageResolver
{
    IEnumerable<PackageManifest> ResolveDependencies(PackageManifest target, IEnumerable<PackageManifest> pool);
}

public interface IPackageManifestLoader
{
    Task<PackageManifest> LoadAsync(string filePath, CancellationToken cancellationToken = default);
    Task<IEnumerable<PackageManifest>> LoadAllAsync(string directoryPath, CancellationToken cancellationToken = default);
}

public interface IRollbackManager
{
    Task RollbackSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}

public interface IInstallationSession
{
    string SessionId { get; }
    DateTime StartedAtUtc { get; }
    Task CompleteSessionAsync(CancellationToken cancellationToken = default);
}
