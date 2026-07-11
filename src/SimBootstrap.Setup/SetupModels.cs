using System;
using System.Collections.Generic;
using System.IO;

namespace SimBootstrap.Setup;

public enum SetupState
{
    Preparing,
    Provisioning,
    InstallingAgent,
    ConfiguringService,
    StartingAgent,
    Verifying,
    Completed,
    Failed
}

public sealed record SetupOptions(string PairingCode, bool NonInteractive = false);

public sealed record SetupStepLog(DateTimeOffset Timestamp, SetupState State, string Message);

public sealed class SetupResult
{
    public bool Success { get; set; }
    public SetupState State { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<SetupStepLog> Logs { get; } = new();
}

public sealed class SetupPaths
{
    public string ProgramFilesAgentDirectory { get; init; } = @"C:\Program Files\SimBootstrap\Agent";
    public string ProgramDataConfigDirectory { get; init; } = @"C:\ProgramData\SimBootstrap\config";
    public string ProgramDataLogsDirectory { get; init; } = @"C:\ProgramData\SimBootstrap\logs";
    public string ProgramDataStateDirectory { get; init; } = @"C:\ProgramData\SimBootstrap\state";
    public string AgentSettingsPath => Path.Combine(ProgramDataConfigDirectory, "agentsettings.json");
    public string InstallationResultPath => Path.Combine(ProgramDataStateDirectory, "installation-result.json");
    public string SetupLogPath => Path.Combine(ProgramDataLogsDirectory, "simbootstrap-setup.log");
    public string AgentExePath => Path.Combine(ProgramFilesAgentDirectory, "SimBootstrap.Agent.exe");
    public string SetupConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "setupconfig.json");
}

public sealed class SetupConfig
{
    public string ControlServerUrl { get; set; } = string.Empty;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ControlServerUrl))
        {
            throw new ArgumentException("setupConfig.controlServerUrl is required.");
        }

        if (!Uri.TryCreate(ControlServerUrl, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("setupConfig.controlServerUrl must be an absolute URI.");
        }

        var isLocalDevelopment =
            uri.IsLoopback ||
            string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

        if (!isLocalDevelopment && uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("setupConfig.controlServerUrl must use HTTPS outside local development.");
        }
    }
}
