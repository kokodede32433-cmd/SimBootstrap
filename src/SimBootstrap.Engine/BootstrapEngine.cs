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
    private readonly IPackageResolver _resolver;

    public BootstrapEngine(
        PackageRegistry registry,
        IPackageVerifier verifier,
        IInstallPlanBuilder planBuilder,
        IInstallerProviderRegistry providerRegistry,
        IPackageResolver resolver)
    {
        _registry = registry;
        _verifier = verifier;
        _planBuilder = planBuilder;
        _providerRegistry = providerRegistry;
        _resolver = resolver;
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

        // Validate root package presence first
        var rootManifest = _registry.GetPackageById(packageId);
        if (rootManifest == null)
        {
            session.Status = "Failed";
            session.CompletedAtUtc = DateTime.UtcNow;
            session.Logs.Add($"Error: Package manifest with ID '{packageId}' not found in registry.");
            return new InstallationResult(false, session.SessionId, "Package not found", session.Logs);
        }

        session.Logs.Add("Resolving package dependencies...");
        var resolutionResult = _resolver.ResolveDependencies(packageId);

        if (resolutionResult.Status == DependencyResolutionStatus.MissingDependency)
        {
            session.Status = "Failed";
            session.CompletedAtUtc = DateTime.UtcNow;
            session.Logs.Add($"Error: Dependency resolution failed. Missing dependency: '{resolutionResult.MissingPackageId}'");
            return new InstallationResult(false, session.SessionId, resolutionResult.ErrorMessage, session.Logs);
        }

        if (resolutionResult.Status == DependencyResolutionStatus.CycleDetected)
        {
            session.Status = "Failed";
            session.CompletedAtUtc = DateTime.UtcNow;
            session.Logs.Add($"Error: Dependency resolution failed. Circular dependency path: {resolutionResult.ErrorMessage}");
            return new InstallationResult(false, session.SessionId, resolutionResult.ErrorMessage, session.Logs);
        }

        session.Logs.Add($"Resolved install order: {string.Join(" -> ", resolutionResult.OrderedPackageIds)}");

        foreach (var depId in resolutionResult.OrderedPackageIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var depManifest = _registry.GetPackageById(depId);
            if (depManifest == null)
            {
                session.Status = "Failed";
                session.CompletedAtUtc = DateTime.UtcNow;
                session.Logs.Add($"Error: Package manifest for resolved package '{depId}' not found.");
                return new InstallationResult(false, session.SessionId, $"Manifest not found for resolved package: {depId}", session.Logs);
            }

            var isInstalled = await _registry.IsPackageInstalledAsync(depId, depManifest.Version, cancellationToken);
            if (isInstalled)
            {
                session.Logs.Add($"Package '{depId}' v{depManifest.Version} is already installed. Skipping.");
                continue;
            }

            session.Logs.Add($"Installing package: '{depId}'...");
            session.Logs.Add($"Loaded manifest: '{depManifest.Name}' v{depManifest.Version}");

            session.Logs.Add("Running mock verification check...");
            var verificationResult = await _verifier.VerifyAsync(depManifest, cancellationToken);
            if (!verificationResult)
            {
                session.Status = "Failed";
                session.CompletedAtUtc = DateTime.UtcNow;
                session.Logs.Add($"Error: Package verification failed for '{depId}'.");
                return new InstallationResult(false, session.SessionId, "Verification failed", session.Logs);
            }

            session.Logs.Add("Verification succeeded. Resolving installer provider...");
            
            if (!Enum.TryParse<InstallerType>(depManifest.InstallerType, true, out var installerType))
            {
                session.Status = "Failed";
                session.CompletedAtUtc = DateTime.UtcNow;
                var error = $"Unsupported installer type: '{depManifest.InstallerType}'";
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
            var sourcePath = $"C:\\SimBootstrap\\Downloads\\{depManifest.Id}_{depManifest.Version}.ext"; // mock source path
            var plan = _planBuilder.BuildPlan(depManifest, sourcePath);
            session.Logs.Add($"Execution Plan Command: '{plan.Command}', Args: '{plan.Arguments}'");

            session.Logs.Add("Executing installer provider...");
            var installRequest = new InstallRequest(depManifest.Id, installerType, sourcePath, depManifest.InstallArguments);
            var providerResult = await provider.InstallAsync(installRequest, cancellationToken);

            foreach (var log in providerResult.Logs)
            {
                session.Logs.Add($"[{depId}] {log}");
            }

            if (!providerResult.IsSuccess)
            {
                session.Status = "Failed";
                session.CompletedAtUtc = DateTime.UtcNow;
                session.Logs.Add($"Error: Installer provider failed with ExitCode: {providerResult.ExitCode}. Message: {providerResult.ErrorMessage}");
                return new InstallationResult(false, session.SessionId, providerResult.ErrorMessage ?? "Installer execution failed", session.Logs);
            }

            // Track local registry state
            await _registry.RegisterInstallationAsync(depManifest.Id, depManifest.Version, cancellationToken);
            session.Logs.Add($"Successfully installed package: '{depId}'");
        }

        await session.CompleteSessionAsync(cancellationToken);
        session.Logs.Add("Installation completed successfully.");
        session.Logs.Add("Overall installation orchestration completed successfully.");

        return new InstallationResult(true, session.SessionId, null, session.Logs);
    }
}
