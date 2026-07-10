using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine;

public abstract class BaseMockInstallerProvider : IInstallerProvider
{
    public abstract InstallerType SupportedType { get; }

    public Task<InstallerInstallResult> InstallAsync(InstallRequest request, CancellationToken cancellationToken = default)
    {
        var logs = new List<string>
        {
            $"Starting mock installation for package '{request.PackageId}' of type '{SupportedType}'...",
            $"Source Path: {request.SourcePath}",
            $"Arguments: {request.InstallArguments}"
        };

        if (request.InstallArguments != null && request.InstallArguments.Contains("fail-install"))
        {
            logs.Add("Mock installation failed (simulated failure).");
            return Task.FromResult(new InstallerInstallResult(false, 1603, "Simulated installation failure", logs));
        }

        logs.Add("Mock installation completed successfully.");
        return Task.FromResult(new InstallerInstallResult(true, 0, null, logs));
    }
}

public class MockMsiInstallerProvider : BaseMockInstallerProvider
{
    public override InstallerType SupportedType => InstallerType.Msi;
}

public class MockExeInstallerProvider : BaseMockInstallerProvider
{
    public override InstallerType SupportedType => InstallerType.Exe;
}

public class MockZipInstallerProvider : BaseMockInstallerProvider
{
    public override InstallerType SupportedType => InstallerType.Zip;
}

public class MockWingetInstallerProvider : BaseMockInstallerProvider
{
    public override InstallerType SupportedType => InstallerType.Winget;
}

public class MockPowerShellInstallerProvider : BaseMockInstallerProvider
{
    public override InstallerType SupportedType => InstallerType.PowerShell;
}

public class MockPortableInstallerProvider : BaseMockInstallerProvider
{
    public override InstallerType SupportedType => InstallerType.Portable;
}
