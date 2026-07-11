using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Agent;

namespace SimBootstrap.Setup;

public sealed class SetupOrchestrator
{
    private readonly ISetupPlatform _platform;
    private readonly IPairingClient _pairingClient;
    private readonly ISetupFileSystem _fileSystem;
    private readonly IAgentPayloadExtractor _payloadExtractor;
    private readonly ISetupProvisioner _provisioner;
    private readonly ISetupServiceManager _serviceManager;
    private readonly SetupPaths _paths;

    public SetupOrchestrator(
        ISetupPlatform platform,
        IPairingClient pairingClient,
        ISetupFileSystem fileSystem,
        IAgentPayloadExtractor payloadExtractor,
        ISetupProvisioner provisioner,
        ISetupServiceManager serviceManager,
        SetupPaths? paths = null)
    {
        _platform = platform;
        _pairingClient = pairingClient;
        _fileSystem = fileSystem;
        _payloadExtractor = payloadExtractor;
        _provisioner = provisioner;
        _serviceManager = serviceManager;
        _paths = paths ?? new SetupPaths();
    }

    public async Task<SetupResult> RunAsync(SetupOptions options, CancellationToken cancellationToken = default)
    {
        var result = new SetupResult();
        var agentPayloadCopied = false;
        var serviceInstallAttempted = false;

        async Task LogAsync(SetupState state, string message)
        {
            result.State = state;
            result.Logs.Add(new SetupStepLog(DateTimeOffset.UtcNow, state, message));
            try
            {
                _fileSystem.CreateDirectory(_paths.ProgramDataLogsDirectory);
                _fileSystem.WriteAllText(_paths.SetupLogPath, string.Join(Environment.NewLine, result.Logs.ConvertAll(l => $"[{l.Timestamp:O}] {l.State}: {l.Message}")));
            }
            catch
            {
                await Task.CompletedTask;
            }
        }

        try
        {
            await LogAsync(SetupState.Preparing, "Preparing setup.");
            if (!_platform.IsWindows)
            {
                throw new InvalidOperationException("SimBootstrap Setup can only run on Windows.");
            }

            if (!_platform.IsAdministrator)
            {
                var relaunched = await _platform.RelaunchElevatedAsync(BuildElevationArguments(options), cancellationToken);
                result.Success = relaunched;
                result.Message = relaunched ? "Elevation requested." : "Administrator privileges are required.";
                return result;
            }

            await _pairingClient.ValidatePairCodeAsync(options.PairCode, cancellationToken);
            await LogAsync(SetupState.Preparing, "Preview pairing validated with mock LOCAL-PAIR-CODE.");

            foreach (var directory in new[]
            {
                _paths.ProgramFilesAgentDirectory,
                _paths.ProgramDataConfigDirectory,
                _paths.ProgramDataLogsDirectory,
                _paths.ProgramDataStateDirectory
            })
            {
                _fileSystem.CreateDirectory(directory);
            }

            await LogAsync(SetupState.Provisioning, "Running provisioning in apply mode.");
            await _provisioner.ProvisionAsync(cancellationToken);

            await LogAsync(SetupState.InstallingAgent, "Extracting Agent payload.");
            await _payloadExtractor.ExtractAsync(_paths.ProgramFilesAgentDirectory, _fileSystem, cancellationToken);
            agentPayloadCopied = true;

            await LogAsync(SetupState.InstallingAgent, "Writing agentsettings.json.");
            WriteAgentSettings(options.PairCode);

            await LogAsync(SetupState.ConfiguringService, "Installing or updating Windows Service.");
            serviceInstallAttempted = true;
            await _serviceManager.InstallOrUpdateAsync(_paths.AgentExePath, _paths.AgentSettingsPath, cancellationToken);

            await LogAsync(SetupState.StartingAgent, "Starting Windows Service.");
            await _serviceManager.StartAsync(cancellationToken);

            await LogAsync(SetupState.Verifying, "Verifying installation.");
            await VerifyAsync(cancellationToken);

            await LogAsync(SetupState.Completed, "Installation completed successfully.");
            result.Success = true;
            result.Message = "Installation completed successfully.";
            WriteInstallationResult(result);
            return result;
        }
        catch (Exception ex)
        {
            await LogAsync(SetupState.Failed, ex.Message);
            result.Success = false;
            result.Message = ex.Message;
            WriteInstallationResult(result);

            if (serviceInstallAttempted || agentPayloadCopied)
            {
                try
                {
                    await _serviceManager.UninstallIfCreatedAsync(cancellationToken);
                    if (agentPayloadCopied)
                    {
                    _fileSystem.DeleteDirectory(_paths.ProgramFilesAgentDirectory, recursive: true);
                    }
                }
                catch
                {
                    // Best-effort rollback for preview installer.
                }
            }

            return result;
        }
    }

    private void WriteAgentSettings(string pairCode)
    {
        if (_fileSystem.FileExists(_paths.AgentSettingsPath))
        {
            var existing = _fileSystem.ReadAllText(_paths.AgentSettingsPath);
            try
            {
                var settings = JsonSerializer.Deserialize<AgentSettings>(existing, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                settings?.Validate();
                return;
            }
            catch
            {
                // Invalid config is replaced below.
            }
        }

        var newSettings = new AgentSettings
        {
            AgentId = Environment.MachineName,
            PairCode = pairCode,
            MockControlServerUrl = "mock://local-control",
            HeartbeatIntervalSeconds = 60
        };
        _fileSystem.WriteAllText(_paths.AgentSettingsPath, JsonSerializer.Serialize(newSettings, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task VerifyAsync(CancellationToken cancellationToken)
    {
        var waitResult = await _serviceManager.WaitForRunningAsync(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(500), cancellationToken);
        if (!waitResult.ReachedRunning)
        {
            throw new InvalidOperationException(
                $"Verification failed: service did not reach Running within 30 seconds. Last observed status: {waitResult.LastStatus}. Elapsed timeout: {FormatElapsed(waitResult.Elapsed)}.");
        }

        var settingsJson = _fileSystem.ReadAllText(_paths.AgentSettingsPath);
        var settings = JsonSerializer.Deserialize<AgentSettings>(settingsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Verification failed: agent config is invalid.");
        settings.Validate();

        if (!_fileSystem.FileExists(_paths.AgentExePath))
        {
            throw new InvalidOperationException("Verification failed: agent process executable is missing.");
        }
    }

    private void WriteInstallationResult(SetupResult result)
    {
        _fileSystem.CreateDirectory(_paths.ProgramDataStateDirectory);
        _fileSystem.WriteAllText(_paths.InstallationResultPath, JsonSerializer.Serialize(result, CreateResultJsonOptions()));
    }

    private static string BuildElevationArguments(SetupOptions options)
    {
        return $"--pair-code \"{options.PairCode}\"";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalSeconds >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalSeconds:0.0} seconds")
            : string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalMilliseconds:0} ms");
    }

    private static JsonSerializerOptions CreateResultJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
