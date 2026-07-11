using System;
using System.Threading;
using System.Threading.Tasks;

namespace SimBootstrap.Setup;

public enum SetupServiceStatus
{
    Missing,
    Unknown,
    Stopped,
    StartPending,
    StopPending,
    Running,
    ContinuePending,
    PausePending,
    Paused
}

public sealed record ServiceWaitResult(bool ReachedRunning, SetupServiceStatus LastStatus, TimeSpan Elapsed);

public interface ISetupPlatform
{
    bool IsWindows { get; }
    bool IsAdministrator { get; }
    string ExecutablePath { get; }
    Task<bool> RelaunchElevatedAsync(string arguments, CancellationToken cancellationToken);
}

public record PairingResponse(string AgentId, string ClubId, string LocationId, string MachineCredential);

public interface IPairingClient
{
    Task<PairingResponse> PairAsync(string pairingCode, string controlServerUrl, string machineId, string machineName, string agentVersion, CancellationToken cancellationToken);
}

public interface ISetupFileSystem
{
    void CreateDirectory(string path);
    bool FileExists(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string contents);
    void CopyFile(string sourcePath, string destinationPath, bool overwrite);
    void MoveFile(string sourcePath, string destinationPath, bool overwrite);
    void DeleteFile(string path);
    void DeleteDirectory(string path, bool recursive);
}

public interface IAgentConfigAcl
{
    Task ApplyRestrictedAclAsync(string filePath, CancellationToken cancellationToken);
}

public interface IAgentPayloadExtractor
{
    Task ExtractAsync(string destinationDirectory, ISetupFileSystem fileSystem, CancellationToken cancellationToken);
}

public interface ISetupProvisioner
{
    Task ProvisionAsync(CancellationToken cancellationToken);
}

public interface ISetupServiceManager
{
    Task InstallOrUpdateAsync(string agentExePath, string configPath, CancellationToken cancellationToken);
    Task StartAsync(CancellationToken cancellationToken);
    Task<ServiceWaitResult> WaitForRunningAsync(TimeSpan timeout, TimeSpan retryInterval, CancellationToken cancellationToken);
    Task UninstallIfCreatedAsync(CancellationToken cancellationToken);
}
