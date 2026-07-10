using System;
using System.Threading;
using System.Threading.Tasks;

namespace SimBootstrap.Engine;

public class BootstrapEngine : IBootstrapEngine
{
    private readonly PackageRegistry _registry;
    private readonly IPackageVerifier _verifier;

    public BootstrapEngine(PackageRegistry registry, IPackageVerifier verifier)
    {
        _registry = registry;
        _verifier = verifier;
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

        session.Logs.Add("Verification succeeded. Simulating package installation...");
        await session.CompleteSessionAsync(cancellationToken);
        session.Logs.Add("Installation completed successfully.");

        // Track local registry state
        await _registry.RegisterInstallationAsync(manifest.Id, manifest.Version, cancellationToken);

        return new InstallationResult(true, session.SessionId, null, session.Logs);
    }
}
