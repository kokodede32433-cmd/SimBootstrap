using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine;

public class BootstrapEngine : IBootstrapEngine
{
    private readonly PackageRegistry _registry;
    private readonly IPackageVerifier _verifier;
    private readonly IInstallPlanBuilder _planBuilder;
    private readonly IInstallerProviderRegistry _providerRegistry;

    public BootstrapEngine(
        PackageRegistry registry,
        IPackageVerifier verifier,
        IInstallPlanBuilder planBuilder,
        IInstallerProviderRegistry providerRegistry)
    {
        _registry = registry;
        _verifier = verifier;
        _planBuilder = planBuilder;
        _providerRegistry = providerRegistry;
    }

    public Task<bool> RunBootstrapAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }

    public async Task<InstallationResult> InstallPackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            throw new ArgumentException("Package ID cannot be null or empty.", nameof(packageId));
        }

        var session = new InstallationSession(packageId)
        {
            Status = "Running"
        };
        session.Logs.Add($"Starting installation orchestration for package ID: '{packageId}'");

        var manifest = _registry.GetPackageById(packageId);
        if (manifest == null)
        {
            session.Status = "Failed";
            session.CompletedAtUtc = DateTime.UtcNow;
            session.Logs.Add($"Error: Package manifest with ID '{packageId}' not found in registry.");
            return new InstallationResult(false, session.SessionId, "Package not found", session.Logs);
        }

        session.Logs.Add($"Loaded manifest: '{manifest.Name}' v{manifest.Version}");

        session.Logs.Add("Running mock verification check...");
        var verificationResult = await _verifier.VerifyAsync(manifest, cancellationToken);
        if (!verificationResult)
        {
            session.Status = "Failed";
            session.CompletedAtUtc = DateTime.UtcNow;
            session.Logs.Add("Error: Package verification failed.");
            return new InstallationResult(false, session.SessionId, "Verification failed", session.Logs);
        }

        session.Logs.Add("Verification succeeded. Resolving installer provider...");
        
        if (!Enum.TryParse<InstallerType>(manifest.InstallerType, true, out var installerType))
        {
            session.Status = "Failed";
            session.CompletedAtUtc = DateTime.UtcNow;
            var error = $"Unsupported installer type: '{manifest.InstallerType}'";
            session.Logs.Add($"Error: {error}");
            return new InstallationResult(false, session.SessionId, error, session.Logs);
        }

        IInstallerProvider provider;
        try
        {
            provider = _providerRegistry.GetProvider(installerType);
        }
        catch (KeyNotFoundException ex)
        {
            session.Status = "Failed";
            session.CompletedAtUtc = DateTime.UtcNow;
            session.Logs.Add($"Error: {ex.Message}");
            return new InstallationResult(false, session.SessionId, ex.Message, session.Logs);
        }

        session.Logs.Add("Building installer execution plan...");
        var sourcePath = $"C:\\SimBootstrap\\Downloads\\{manifest.Id}_{manifest.Version}.ext"; // mock source path
        var plan = _planBuilder.BuildPlan(manifest, sourcePath);
        session.Logs.Add($"Execution Plan Command: '{plan.Command}', Args: '{plan.Arguments}'");

        session.Logs.Add("Executing installer provider...");
        var installRequest = new InstallRequest(manifest.Id, installerType, sourcePath, manifest.InstallArguments);
        var providerResult = await provider.InstallAsync(installRequest, cancellationToken);

        foreach (var log in providerResult.Logs)
        {
            session.Logs.Add($"[Installer] {log}");
        }

        if (!providerResult.IsSuccess)
        {
            session.Status = "Failed";
            session.CompletedAtUtc = DateTime.UtcNow;
            session.Logs.Add($"Error: Installer provider failed with ExitCode: {providerResult.ExitCode}. Message: {providerResult.ErrorMessage}");
            return new InstallationResult(false, session.SessionId, providerResult.ErrorMessage ?? "Installer execution failed", session.Logs);
        }

        await session.CompleteSessionAsync(cancellationToken);
        session.Logs.Add("Installation completed successfully.");

        // Track local registry state
        await _registry.RegisterInstallationAsync(manifest.Id, manifest.Version, cancellationToken);

        return new InstallationResult(true, session.SessionId, null, session.Logs);
    }
}
