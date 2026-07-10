using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SimBootstrap.Contracts;
using SimBootstrap.Engine;
using Xunit;

namespace SimBootstrap.Tests.Unit;

public class DependencyResolutionTests
{
    private readonly string _tempDir;
    private readonly PackageManifestLoader _loader;
    private readonly PackageRegistry _registry;
    private readonly DependencyGraphBuilder _graphBuilder;
    private readonly DependencyCycleDetector _cycleDetector;
    private readonly PackageResolver _resolver;

    public DependencyResolutionTests()
    {
        _tempDir = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _loader = new PackageManifestLoader();
        _registry = new PackageRegistry(_loader);
        _graphBuilder = new DependencyGraphBuilder(_registry);
        _cycleDetector = new DependencyCycleDetector();
        _resolver = new PackageResolver(_graphBuilder, _cycleDetector);
    }

    private async Task WriteManifestAsync(PackageManifest manifest)
    {
        var path = Path.Combine(_tempDir, $"{manifest.Id}.json");
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(manifest));
    }

    private async Task LoadAllAsync()
    {
        await _registry.DiscoverManifestsAsync(_tempDir);
    }

    [Fact]
    public async Task Resolve_ShouldReturnRootPackageOnly_WhenNoDependencies()
    {
        // Arrange
        var pkg = new PackageManifest("pkg-a", "A", "1.0", "msi", "http://a", "", new(), new());
        await WriteManifestAsync(pkg);
        await LoadAllAsync();

        // Act
        var result = _resolver.ResolveDependencies("pkg-a");

        // Assert
        Assert.Equal(DependencyResolutionStatus.Success, result.Status);
        var order = Assert.Single(result.OrderedPackageIds);
        Assert.Equal("pkg-a", order);
    }

    [Fact]
    public async Task Resolve_ShouldReturnCorrectOrder_WhenSingleDependency()
    {
        // Arrange
        var a = new PackageManifest("pkg-a", "A", "1.0", "msi", "http://a", "", new(), new() { "pkg-b" });
        var b = new PackageManifest("pkg-b", "B", "1.0", "msi", "http://b", "", new(), new());
        await WriteManifestAsync(a);
        await WriteManifestAsync(b);
        await LoadAllAsync();

        // Act
        var result = _resolver.ResolveDependencies("pkg-a");

        // Assert
        Assert.Equal(DependencyResolutionStatus.Success, result.Status);
        Assert.Equal(2, result.OrderedPackageIds.Count);
        Assert.Equal("pkg-b", result.OrderedPackageIds[0]);
        Assert.Equal("pkg-a", result.OrderedPackageIds[1]);
    }

    [Fact]
    public async Task Resolve_ShouldReturnCorrectOrder_WhenTransitiveDependencies()
    {
        // Arrange
        var a = new PackageManifest("pkg-a", "A", "1.0", "msi", "http://a", "", new(), new() { "pkg-b" });
        var b = new PackageManifest("pkg-b", "B", "1.0", "msi", "http://b", "", new(), new() { "pkg-c" });
        var c = new PackageManifest("pkg-c", "C", "1.0", "msi", "http://c", "", new(), new());
        await WriteManifestAsync(a);
        await WriteManifestAsync(b);
        await WriteManifestAsync(c);
        await LoadAllAsync();

        // Act
        var result = _resolver.ResolveDependencies("pkg-a");

        // Assert
        Assert.Equal(DependencyResolutionStatus.Success, result.Status);
        Assert.Equal(3, result.OrderedPackageIds.Count);
        Assert.Equal("pkg-c", result.OrderedPackageIds[0]);
        Assert.Equal("pkg-b", result.OrderedPackageIds[1]);
        Assert.Equal("pkg-a", result.OrderedPackageIds[2]);
    }

    [Fact]
    public async Task Resolve_ShouldDeduplicateSharedDependency()
    {
        // Arrange
        // A depends on B and C. Both B and C depend on D.
        var a = new PackageManifest("pkg-a", "A", "1.0", "msi", "http://a", "", new(), new() { "pkg-b", "pkg-c" });
        var b = new PackageManifest("pkg-b", "B", "1.0", "msi", "http://b", "", new(), new() { "pkg-d" });
        var c = new PackageManifest("pkg-c", "C", "1.0", "msi", "http://c", "", new(), new() { "pkg-d" });
        var d = new PackageManifest("pkg-d", "D", "1.0", "msi", "http://d", "", new(), new());
        await WriteManifestAsync(a);
        await WriteManifestAsync(b);
        await WriteManifestAsync(c);
        await WriteManifestAsync(d);
        await LoadAllAsync();

        // Act
        var result = _resolver.ResolveDependencies("pkg-a");

        // Assert
        Assert.Equal(DependencyResolutionStatus.Success, result.Status);
        Assert.Equal(4, result.OrderedPackageIds.Count);
        Assert.Equal("pkg-d", result.OrderedPackageIds[0]);
        Assert.Contains("pkg-b", result.OrderedPackageIds);
        Assert.Contains("pkg-c", result.OrderedPackageIds);
        Assert.Equal("pkg-a", result.OrderedPackageIds[3]);
        
        // Assert D is only listed once
        Assert.Single(result.OrderedPackageIds.FindAll(x => x == "pkg-d"));
    }

    [Fact]
    public async Task Resolve_ShouldFail_WhenDependencyIsMissing()
    {
        // Arrange
        var a = new PackageManifest("pkg-a", "A", "1.0", "msi", "http://a", "", new(), new() { "pkg-missing" });
        await WriteManifestAsync(a);
        await LoadAllAsync();

        // Act
        var result = _resolver.ResolveDependencies("pkg-a");

        // Assert
        Assert.Equal(DependencyResolutionStatus.MissingDependency, result.Status);
        Assert.Equal("pkg-missing", result.MissingPackageId);
    }

    [Fact]
    public async Task Resolve_ShouldFail_WhenCircularDependencyExists()
    {
        // Arrange
        var a = new PackageManifest("pkg-a", "A", "1.0", "msi", "http://a", "", new(), new() { "pkg-b" });
        var b = new PackageManifest("pkg-b", "B", "1.0", "msi", "http://b", "", new(), new() { "pkg-a" });
        await WriteManifestAsync(a);
        await WriteManifestAsync(b);
        await LoadAllAsync();

        // Act
        var result = _resolver.ResolveDependencies("pkg-a");

        // Assert
        Assert.Equal(DependencyResolutionStatus.CycleDetected, result.Status);
        Assert.NotNull(result.CyclePath);
        Assert.Contains("pkg-a", result.CyclePath);
        Assert.Contains("pkg-b", result.CyclePath);
    }

    [Fact]
    public async Task BootstrapEngine_ShouldExecuteDependencyPlanInCorrectOrder()
    {
        // Arrange
        var a = new PackageManifest("pkg-a", "A", "1.0", "msi", "http://a", "", new(), new() { "pkg-b" });
        var b = new PackageManifest("pkg-b", "B", "1.0", "msi", "http://b", "", new(), new());
        await WriteManifestAsync(a);
        await WriteManifestAsync(b);
        await LoadAllAsync();

        var verifier = new PackageVerifier();
        var planBuilder = new InstallPlanBuilder();
        var providerRegistry = new InstallerProviderRegistry();
        providerRegistry.Register(new MockMsiInstallerProvider());

        var engine = new BootstrapEngine(_registry, verifier, planBuilder, providerRegistry, _resolver);

        // Act
        var result = await engine.InstallPackageAsync("pkg-a");

        // Assert
        Assert.True(result.IsSuccess);
        
        // Assert log contains sequential mock execution markers
        var bInstallIndex = result.Logs.FindIndex(l => l.Contains("[pkg-b] Mock installation completed successfully."));
        var aInstallIndex = result.Logs.FindIndex(l => l.Contains("[pkg-a] Mock installation completed successfully."));
        
        Assert.True(bInstallIndex >= 0);
        Assert.True(aInstallIndex >= 0);
        Assert.True(bInstallIndex < aInstallIndex);
    }
}
