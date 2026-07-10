using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SimBootstrap.Contracts;
using SimBootstrap.Engine;
using Xunit;

namespace SimBootstrap.Tests.Unit;

public class InstallerEngineTests
{
    [Fact]
    public void BuildPlan_ShouldBuildCorrectMsiExecutionPlan()
    {
        // Arrange
        var builder = new InstallPlanBuilder();
        var manifest = new PackageManifest(
            "test-msi",
            "Test MSI Package",
            "1.0.0",
            "msi",
            "http://example.com/test.msi",
            "/qn /norestart",
            new List<VerifyRule>(),
            new List<string>()
        );
        var sourcePath = @"C:\SimBootstrap\Downloads\test-msi_1.0.0.msi";

        // Act
        var plan = builder.BuildPlan(manifest, sourcePath);

        // Assert
        Assert.Equal("test-msi", plan.PackageId);
        Assert.Equal(InstallerType.Msi, plan.InstallerType);
        Assert.Equal("msiexec.exe", plan.Command);
        Assert.Equal($"/i \"{sourcePath}\" /qn /norestart", plan.Arguments);
    }

    [Fact]
    public void BuildPlan_ShouldBuildCorrectExeExecutionPlan()
    {
        // Arrange
        var builder = new InstallPlanBuilder();
        var manifest = new PackageManifest(
            "test-exe",
            "Test EXE Package",
            "2.5.0",
            "exe",
            "http://example.com/setup.exe",
            "/silent",
            new List<VerifyRule>(),
            new List<string>()
        );
        var sourcePath = @"C:\SimBootstrap\Downloads\setup.exe";

        // Act
        var plan = builder.BuildPlan(manifest, sourcePath);

        // Assert
        Assert.Equal("test-exe", plan.PackageId);
        Assert.Equal(InstallerType.Exe, plan.InstallerType);
        Assert.Equal(sourcePath, plan.Command);
        Assert.Equal("/silent", plan.Arguments);
    }

    [Fact]
    public void BuildPlan_ShouldThrowNotSupportedException_WhenTypeIsUnsupported()
    {
        // Arrange
        var builder = new InstallPlanBuilder();
        var manifest = new PackageManifest(
            "bad-type",
            "Bad Package",
            "1.0.0",
            "invalid_format_type",
            "http://example.com/bad.bin",
            "",
            new List<VerifyRule>(),
            new List<string>()
        );

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => builder.BuildPlan(manifest, "dummy"));
    }

    [Fact]
    public void Registry_ShouldThrowKeyNotFoundException_WhenProviderIsMissing()
    {
        // Arrange
        var registry = new InstallerProviderRegistry();

        // Act & Assert
        Assert.Throws<KeyNotFoundException>(() => registry.GetProvider(InstallerType.Msi));
    }

    [Fact]
    public async Task MockMsiProvider_ShouldSucceed_ByDefault()
    {
        // Arrange
        var provider = new MockMsiInstallerProvider();
        var request = new InstallRequest("test-pkg", InstallerType.Msi, "dummy-path", "/quiet");

        // Act
        var result = await provider.InstallAsync(request);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task MockMsiProvider_ShouldFail_WhenFailInstallArgumentPassed()
    {
        // Arrange
        var provider = new MockMsiInstallerProvider();
        var request = new InstallRequest("test-pkg", InstallerType.Msi, "dummy-path", "fail-install");

        // Act
        var result = await provider.InstallAsync(request);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(1603, result.ExitCode);
        Assert.Equal("Simulated installation failure", result.ErrorMessage);
    }

    [Fact]
    public async Task BootstrapEngine_ShouldOrchestrateInstallerProvider_DuringInstall()
    {
        // Arrange
        var loader = new PackageManifestLoader();
        var registry = new PackageRegistry(loader);
        
        // Populate package manifest in registry
        var manifest = new PackageManifest(
            "valid-msi",
            "Test Package",
            "1.0.0",
            "msi",
            "http://example.com/file.msi",
            "/quiet",
            new List<VerifyRule>(),
            new List<string>()
        );
        var tempDir = Path.Combine(AppContext.BaseDirectory, "TestOrchestrateDir");
        if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
        var manifestPath = Path.Combine(tempDir, "valid-msi.json");
        await File.WriteAllTextAsync(manifestPath, System.Text.Json.JsonSerializer.Serialize(manifest));
        await registry.DiscoverManifestsAsync(tempDir);

        var verifier = new PackageVerifier();
        var planBuilder = new InstallPlanBuilder();
        var providerRegistry = new InstallerProviderRegistry();
        providerRegistry.Register(new MockMsiInstallerProvider());

        var engine = new BootstrapEngine(registry, verifier, planBuilder, providerRegistry);

        // Act
        var result = await engine.InstallPackageAsync("valid-msi");

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Contains(result.Logs, l => l.Contains("[Installer] Mock installation completed successfully."));
    }
}
