using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text;
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

public sealed class RealPairingClient : IPairingClient
{
    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient _httpClient;

    public RealPairingClient()
        : this(SharedHttpClient)
    {
    }

    public RealPairingClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<PairingResponse> PairAsync(
        string pairingCode,
        string controlServerUrl,
        string machineId,
        string machineName,
        string agentVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pairingCode))
        {
            throw new ArgumentException("Pair code cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(controlServerUrl))
        {
            throw new ArgumentException("Control server URL cannot be empty.");
        }

        var requestBody = new
        {
            pairCode = pairingCode,
            machineId = machineId,
            machineName = machineName,
            agentVersion = agentVersion
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var response = await _httpClient.PostAsync(controlServerUrl, content, cancellationToken);
            var responseString = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                try
                {
                    var payload = JsonSerializer.Deserialize<PairingResponsePayload>(responseString, options);
                    if (payload == null ||
                        string.IsNullOrWhiteSpace(payload.AgentId) ||
                        string.IsNullOrWhiteSpace(payload.ClubId) ||
                        string.IsNullOrWhiteSpace(payload.LocationId) ||
                        string.IsNullOrWhiteSpace(payload.MachineCredential))
                    {
                        throw new InvalidOperationException("Failed to parse registration credentials from the pairing service.");
                    }

                    return new PairingResponse(
                        payload.AgentId,
                        payload.ClubId,
                        payload.LocationId,
                        payload.MachineCredential
                    );
                }
                catch (JsonException)
                {
                    throw new InvalidOperationException("Failed to parse registration credentials from the pairing service.");
                }
            }

            // Handle API error codes
            string errorCode = "INTERNAL_ERROR";
            string errorMessage = "An unknown error occurred during pairing.";

            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var errorPayload = JsonSerializer.Deserialize<ErrorResponsePayload>(responseString, options);
                if (errorPayload?.Error != null)
                {
                    errorCode = errorPayload.Error.Code ?? "INTERNAL_ERROR";
                    errorMessage = errorPayload.Error.Message ?? errorMessage;
                }
            }
            catch
            {
                // Fallback to generic status error if JSON parsing fails
            }

            throw errorCode switch
            {
                "INVALID_PAIR_CODE" => new InvalidOperationException("Pairing code is invalid."),
                "PAIR_CODE_EXPIRED" => new InvalidOperationException("Pairing code has expired."),
                "PAIR_CODE_ALREADY_USED" => new InvalidOperationException("Pairing code has already been used."),
                "MACHINE_ALREADY_PAIRED" => new InvalidOperationException("This machine has already been paired."),
                "PAIRING_RATE_LIMITED" => new InvalidOperationException("Too many pairing attempts. Please try again later."),
                "PAIRING_UNAVAILABLE" => new InvalidOperationException("Pairing service is currently unavailable."),
                "INTERNAL_ERROR" => new InvalidOperationException("Pairing service returned an internal error."),
                _ => new InvalidOperationException($"Pairing failed with error code {errorCode}.")
            };
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("Network connection failed. Please verify internet connectivity and try again.", ex);
        }
    }

    private sealed class PairingResponsePayload
    {
        public string AgentId { get; set; } = string.Empty;
        public string? ClubId { get; set; }
        public string? LocationId { get; set; }
        public string MachineCredential { get; set; } = string.Empty;
    }

    private sealed class ErrorResponsePayload
    {
        public ErrorDetails? Error { get; set; }
    }

    private sealed class ErrorDetails
    {
        public string? Code { get; set; }
        public string? Message { get; set; }
    }
}

public sealed class SetupFileSystem : ISetupFileSystem
{
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public bool FileExists(string path) => File.Exists(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public void WriteAllText(string path, string contents) => File.WriteAllText(path, contents);
    public void CopyFile(string sourcePath, string destinationPath, bool overwrite) => File.Copy(sourcePath, destinationPath, overwrite);
    public void MoveFile(string sourcePath, string destinationPath, bool overwrite) => File.Move(sourcePath, destinationPath, overwrite);
    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive);
        }
    }
}

public sealed class WindowsAgentConfigAcl : IAgentConfigAcl
{
    private readonly IProcessRunner _processRunner;

    public WindowsAgentConfigAcl(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task ApplyRestrictedAclAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var arguments = $"\"{filePath}\" /inheritance:r /grant:r *S-1-5-18:(F) *S-1-5-32-544:(F)";
        var result = await _processRunner.RunAsync("icacls.exe", arguments, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("Failed to restrict agent settings ACL.");
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

public interface IWindowsServiceStatusReader
{
    Task<SetupServiceStatus> GetStatusAsync(string serviceName, CancellationToken cancellationToken);
}

public sealed class WindowsServiceStatusReader : IWindowsServiceStatusReader
{
    public Task<SetupServiceStatus> GetStatusAsync(string serviceName, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(SetupServiceStatus.Unknown);
        }

        return Task.FromResult(GetWindowsStatus(serviceName));
    }

    [SupportedOSPlatform("windows")]
    private static SetupServiceStatus GetWindowsStatus(string serviceName)
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            return controller.Status switch
            {
                ServiceControllerStatus.Stopped => SetupServiceStatus.Stopped,
                ServiceControllerStatus.StartPending => SetupServiceStatus.StartPending,
                ServiceControllerStatus.StopPending => SetupServiceStatus.StopPending,
                ServiceControllerStatus.Running => SetupServiceStatus.Running,
                ServiceControllerStatus.ContinuePending => SetupServiceStatus.ContinuePending,
                ServiceControllerStatus.PausePending => SetupServiceStatus.PausePending,
                ServiceControllerStatus.Paused => SetupServiceStatus.Paused,
                _ => SetupServiceStatus.Unknown
            };
        }
        catch (InvalidOperationException)
        {
            return SetupServiceStatus.Missing;
        }
    }
}

public sealed class AgentServiceSetupManager : ISetupServiceManager
{
    private readonly WindowsServiceManager _serviceManager;
    private readonly IWindowsServiceStatusReader _statusReader;

    public AgentServiceSetupManager(IProcessRunner processRunner, IWindowsServiceStatusReader? statusReader = null)
    {
        _statusReader = statusReader ?? new WindowsServiceStatusReader();
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

    public async Task<ServiceWaitResult> WaitForRunningAsync(TimeSpan timeout, TimeSpan retryInterval, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var lastStatus = SetupServiceStatus.Unknown;

        while (stopwatch.Elapsed <= timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastStatus = await _statusReader.GetStatusAsync(AgentPaths.ServiceName, cancellationToken);
            if (lastStatus == SetupServiceStatus.Running)
            {
                return new ServiceWaitResult(true, lastStatus, stopwatch.Elapsed);
            }

            if (lastStatus == SetupServiceStatus.Missing)
            {
                return new ServiceWaitResult(false, lastStatus, stopwatch.Elapsed);
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remaining < retryInterval ? remaining : retryInterval, cancellationToken);
        }

        return new ServiceWaitResult(false, lastStatus, stopwatch.Elapsed);
    }

    public async Task UninstallIfCreatedAsync(CancellationToken cancellationToken)
    {
        await _serviceManager.ExecuteAsync(AgentServiceCommandKind.Uninstall, string.Empty, string.Empty, cancellationToken);
    }
}
