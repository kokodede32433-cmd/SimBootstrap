using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine;

public interface IInstallerProvider
{
    InstallerType SupportedType { get; }
    Task<InstallerInstallResult> InstallAsync(InstallRequest request, CancellationToken cancellationToken = default);
}

public interface IInstallerProviderRegistry
{
    void Register(IInstallerProvider provider);
    IInstallerProvider GetProvider(InstallerType type);
}

public interface IInstallPlanBuilder
{
    InstallerExecutionPlan BuildPlan(PackageManifest manifest, string sourcePath);
}
