using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Engine;
using Xunit;

namespace SimBootstrap.Tests.Unit;

public class PackageEngineTests
{
    private readonly string _testManifestsDir;

    public PackageEngineTests()
    {
        // Path relative to execution context
        _testManifestsDir = Path.Combine(AppContext.BaseDirectory, "TestManifests");
        
        // Ensure test manifests exist locally in copy output folder
        if (!Directory.Exists(_testManifestsDir))
        {
            Directory.CreateDirectory(_testManifestsDir);
        }

        // Copy source test manifests into target dir if needed
        var sourceDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestManifests");
        if (Directory.Exists(sourceDir))
        {
            foreach (var file in Directory.GetFiles(sourceDir, "*.json"))
            {
                var dest = Path.Combine(_testManifestsDir, Path.GetFileName(file));
                File.Copy(file, dest, true);
            }
        }
    }

    [Fact]
    public async Task LoadAsync_ShouldLoadValidManifest_WhenJsonIsValid()
    {
        // Arrange
        var loader = new PackageManifestLoader();
        var filePath = Path.Combine(_testManifestsDir, "valid.json");

        // Act
        var manifest = await loader.LoadAsync(filePath);

        // Assert
        Assert.NotNull(manifest);
        Assert.Equal("valid-pkg", manifest.Id);
        Assert.Equal("Valid Test Package", manifest.Name);
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Equal("msi", manifest.InstallerType);
        Assert.Equal("https://example.com/valid.msi", manifest.DownloadUrl);
    }

    [Fact]
    public async Task LoadAsync_ShouldThrowInvalidOperationException_WhenJsonIsMalformed()
    {
        // Arrange
        var loader = new PackageManifestLoader();
        var filePath = Path.Combine(_testManifestsDir, "invalid.json");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => loader.LoadAsync(filePath));
    }

    [Fact]
    public async Task LoadAsync_ShouldThrowArgumentException_WhenRequiredFieldsAreMissing()
    {
        // Arrange
        var loader = new PackageManifestLoader();
        var filePath = Path.Combine(_testManifestsDir, "missing_fields.json");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => loader.LoadAsync(filePath));
    }

    [Fact]
    public async Task Registry_ShouldDiscoverManifests_WhenScanningDirectory()
    {
        // Arrange
        var loader = new PackageManifestLoader();
        var registry = new PackageRegistry(loader);

        // Act
        await registry.DiscoverManifestsAsync(_testManifestsDir);
        var packages = registry.GetAvailablePackages().ToList();

        // Assert
        Assert.NotEmpty(packages);
        Assert.Contains(packages, p => p.Id == "valid-pkg");
        Assert.Contains(packages, p => p.Id == "fail-verify-pkg");
    }

    [Fact]
    public async Task Registry_ShouldGetPackageById_WhenExists()
    {
        // Arrange
        var loader = new PackageManifestLoader();
        var registry = new PackageRegistry(loader);
        await registry.DiscoverManifestsAsync(_testManifestsDir);

        // Act
        var pkg = registry.GetPackageById("valid-pkg");

        // Assert
        Assert.NotNull(pkg);
        Assert.Equal("Valid Test Package", pkg.Name);
    }

    [Fact]
    public async Task Engine_ShouldInstallSuccessfully_WhenManifestAndVerifySucceed()
    {
        // Arrange
        var loader = new PackageManifestLoader();
        var registry = new PackageRegistry(loader);
        await registry.DiscoverManifestsAsync(_testManifestsDir);
        var verifier = new PackageVerifier();
        var engine = new BootstrapEngine(registry, verifier);

        // Act
        var result = await engine.InstallPackageAsync("valid-pkg");

        // Assert
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.SessionId);
        Assert.Null(result.ErrorMessage);
        Assert.Contains(result.Logs, l => l.Contains("Installation completed successfully"));

        // Verify it was logged as installed
        var isInstalled = await registry.IsPackageInstalledAsync("valid-pkg", "1.2.3");
        Assert.True(isInstalled);
    }

    [Fact]
    public async Task Engine_ShouldFail_WhenVerifierFails()
    {
        // Arrange
        var loader = new PackageManifestLoader();
        var registry = new PackageRegistry(loader);
        await registry.DiscoverManifestsAsync(_testManifestsDir);
        var verifier = new PackageVerifier();
        var engine = new BootstrapEngine(registry, verifier);

        // Act
        var result = await engine.InstallPackageAsync("fail-verify-pkg");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("Verification failed", result.ErrorMessage);
        Assert.Contains(result.Logs, l => l.Contains("verification failed"));
    }

    [Fact]
    public async Task Engine_ShouldReturnNotFound_WhenPackageIdDoesNotExist()
    {
        // Arrange
        var loader = new PackageManifestLoader();
        var registry = new PackageRegistry(loader);
        await registry.DiscoverManifestsAsync(_testManifestsDir);
        var verifier = new PackageVerifier();
        var engine = new BootstrapEngine(registry, verifier);

        // Act
        var result = await engine.InstallPackageAsync("unknown-pkg");

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("Package not found", result.ErrorMessage);
    }
}
