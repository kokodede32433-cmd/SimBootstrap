using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Agent;
using SimBootstrap.Engine.Provisioning;

namespace SimBootstrap.Setup;

public sealed class SetupPlatform : ISetupPlatform
{
    public bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public bool IsAdministrator
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            return IsCurrentProcessAdministrator();
        }
    }

    public string ExecutablePath => Environment.ProcessPath ?? "SimBootstrap.Setup.exe";

    public Task<bool> RelaunchElevatedAsync(string arguments, CancellationToken cancellationToken)
    {
        if (!IsWindows)
        {
            return Task.FromResult(false);
        }

        Process.Start(new ProcessStartInfo(ExecutablePath, arguments)
        {
            UseShellExecute = true,
            Verb = "runas"
        });
        return Task.FromResult(true);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsCurrentProcessAdministrator()
    {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}

public sealed class PreviewPairingClient : IPairingClient
{
    public Task ValidatePairCodeAsync(string pairCode, CancellationToken cancellationToken)
    {
        if (!pairCode.Equals("LOCAL-PAIR-CODE", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Preview builds only accept LOCAL-PAIR-CODE.");
        }

        return Task.CompletedTask;
    }
}

public sealed class SetupFileSystem : ISetupFileSystem
{
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public bool FileExists(string path) => File.Exists(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);
    public void CopyFile(string sourcePath, string destinationPath, bool overwrite) => File.Copy(sourcePath, destinationPath, overwrite);
    public void DeleteDirectory(string path, bool recursive)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive);
        }
    }
}

public sealed class EmbeddedAgentPayloadExtractor : IAgentPayloadExtractor
{
    private const string ResourceName = "SimBootstrap.Agent.Payload.zip";

    public async Task ExtractAsync(string destinationDirectory, ISetupFileSystem fileSystem, CancellationToken cancellationToken)
    {
        fileSystem.CreateDirectory(destinationDirectory);
        var assembly = Assembly.GetExecutingAssembly();
        await using var resource = assembly.GetManifestResourceStream(ResourceName);
        if (resource is not null)
        {
            using var archive = new ZipArchive(resource, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                var relativeName = entry.FullName.Contains('/')
                    ? entry.FullName[(entry.FullName.IndexOf('/') + 1)..]
                    : entry.FullName;
                if (string.IsNullOrWhiteSpace(relativeName))
                {
                    continue;
                }

                var destinationPath = Path.Combine(destinationDirectory, relativeName);
                fileSystem.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                await using var entryStream = entry.Open();
                await using var fileStream = File.Create(destinationPath);
                await entryStream.CopyToAsync(fileStream, cancellationToken);
            }
            return;
        }

        var payloadDir = Path.Combine(AppContext.BaseDirectory, "AgentPayload");
        var sourceExe = Path.Combine(payloadDir, "SimBootstrap.Agent.exe");
        if (!fileSystem.FileExists(sourceExe))
        {
            throw new FileNotFoundException("Agent payload was not embedded and AgentPayload fallback is missing.", sourceExe);
        }

        foreach (var sourcePath in Directory.GetFiles(payloadDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(payloadDir, sourcePath);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            fileSystem.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            fileSystem.CopyFile(sourcePath, destinationPath, overwrite: true);
        }
    }
}

public sealed class ProvisioningSetupProvisioner : ISetupProvisioner
{
    public async Task ProvisionAsync(CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "config", "provisioning.json");
        if (!File.Exists(configPath))
        {
            return;
        }

        var json = await File.ReadAllTextAsync(configPath, cancellationToken);
        var config = JsonSerializer.Deserialize<ProvisioningConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to deserialize provisioning config.");
        var runner = new PowerShellRunner();
        var engine = new ProvisioningEngine(runner, new WindowsCapabilityChecker(runner));
        var result = await engine.RunProvisioningAsync(config, dryRun: false, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException("Provisioning failed.");
        }
    }
}

public sealed class AgentServiceSetupManager : ISetupServiceManager
{
    private readonly WindowsServiceManager _serviceManager;
    private readonly IProcessRunner _processRunner;

    public AgentServiceSetupManager(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
        _serviceManager = new WindowsServiceManager(processRunner);
    }

    public async Task InstallOrUpdateAsync(string agentExePath, string configPath, CancellationToken cancellationToken)
    {
        var result = await _serviceManager.ExecuteAsync(AgentServiceCommandKind.Install, agentExePath, configPath, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Message);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var result = await _serviceManager.ExecuteAsync(AgentServiceCommandKind.Start, string.Empty, string.Empty, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Message);
        }
    }

    public async Task<bool> ExistsAsync(CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync("sc.exe", "query SimBootstrapAgent", cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync("sc.exe", "query SimBootstrapAgent", cancellationToken);
        return result.StdOut.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    public async Task UninstallIfCreatedAsync(CancellationToken cancellationToken)
    {
        await _serviceManager.ExecuteAsync(AgentServiceCommandKind.Uninstall, string.Empty, string.Empty, cancellationToken);
    }
}
