using System;
using System.IO;
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
    private readonly IAgentConfigAcl _agentConfigAcl;
    private readonly SetupPaths _paths;

    public SetupOrchestrator(
        ISetupPlatform platform,
        IPairingClient pairingClient,
        ISetupFileSystem fileSystem,
        IAgentPayloadExtractor payloadExtractor,
        ISetupProvisioner provisioner,
        ISetupServiceManager serviceManager,
        IAgentConfigAcl agentConfigAcl,
        SetupPaths? paths = null)
    {
        _platform = platform;
        _pairingClient = pairingClient;
        _fileSystem = fileSystem;
        _payloadExtractor = payloadExtractor;
        _provisioner = provisioner;
        _serviceManager = serviceManager;
        _agentConfigAcl = agentConfigAcl;
        _paths = paths ?? new SetupPaths();
    }

    public async Task<SetupResult> RunAsync(SetupOptions options, CancellationToken cancellationToken = default)
    {
        var result = new SetupResult();
        var agentPayloadCopied = false;
        var serviceInstallAttempted = false;
        PairingResponse? pairingResponse = null;

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

            var config = LoadSetupConfig();
            config.Validate();

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
            ValidateAgentPayload();
            agentPayloadCopied = true;

            await LogAsync(SetupState.Preparing, "Connecting to pairing service...");
            pairingResponse = await _pairingClient.PairAsync(
                options.PairingCode,
                config.ControlServerUrl,
                Environment.MachineName,
                Environment.MachineName,
                "1.0.0",
                cancellationToken);

            await LogAsync(SetupState.Preparing, "Pairing successful.");

            await LogAsync(SetupState.InstallingAgent, "Writing agentsettings.json.");
            await WriteAgentSettingsAsync(pairingResponse, config.ControlServerUrl, cancellationToken);

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
            var safeMessage = RedactSensitive(ex.Message, options.PairingCode, pairingResponse?.MachineCredential);
            await LogAsync(SetupState.Failed, safeMessage);
            result.Success = false;
            result.Message = safeMessage;
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

    private SetupConfig LoadSetupConfig()
    {
        if (_fileSystem.FileExists(_paths.SetupConfigPath))
        {
            try
            {
                var content = _fileSystem.ReadAllText(_paths.SetupConfigPath);
                var config = JsonSerializer.Deserialize<SetupConfig>(content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (config != null)
                {
                    return config;
                }
            }
            catch
            {
                // Fallback to default
            }
        }

        return new SetupConfig();
    }

    private async Task WriteAgentSettingsAsync(PairingResponse pairing, string controlServerUrl, CancellationToken cancellationToken)
    {
        var newSettings = new AgentSettings
        {
            AgentId = pairing.AgentId,
            LocationId = pairing.LocationId,
            ClubId = pairing.ClubId,
            MachineCredential = pairing.MachineCredential,
            ControlServerUrl = controlServerUrl,
            HeartbeatIntervalSeconds = 60
        };
        newSettings.Validate();
        _fileSystem.CreateDirectory(_paths.ProgramDataConfigDirectory);
        var tempPath = $"{_paths.AgentSettingsPath}.tmp";
        try
        {
            _fileSystem.WriteAllText(tempPath, JsonSerializer.Serialize(newSettings, new JsonSerializerOptions { WriteIndented = true }));
            await _agentConfigAcl.ApplyRestrictedAclAsync(tempPath, cancellationToken);
            _fileSystem.MoveFile(tempPath, _paths.AgentSettingsPath, overwrite: true);
        }
        catch
        {
            _fileSystem.DeleteFile(tempPath);
            throw;
        }
    }

    private void ValidateAgentPayload()
    {
        if (!_fileSystem.FileExists(_paths.AgentExePath))
        {
            throw new InvalidOperationException("Agent payload validation failed: agent process executable is missing.");
        }
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
        return $"--pair-code \"{options.PairingCode}\"";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalSeconds >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalSeconds:0.0} seconds")
            : string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalMilliseconds:0} ms");
    }

    private static string RedactSensitive(string message, params string?[] sensitiveValues)
    {
        var redacted = message;
        foreach (var value in sensitiveValues)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                redacted = redacted.Replace(value, "[REDACTED]", StringComparison.Ordinal);
            }
        }

        return redacted;
    }

    private static JsonSerializerOptions CreateResultJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
