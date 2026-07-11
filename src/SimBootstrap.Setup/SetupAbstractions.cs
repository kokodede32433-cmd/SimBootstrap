using System.Threading;
using System.Threading.Tasks;

namespace SimBootstrap.Setup;

public interface ISetupPlatform
{
    bool IsWindows { get; }
    bool IsAdministrator { get; }
    string ExecutablePath { get; }
    Task<bool> RelaunchElevatedAsync(string arguments, CancellationToken cancellationToken);
}

public interface IPairingClient
{
    Task ValidatePairCodeAsync(string pairCode, CancellationToken cancellationToken);
}

public interface ISetupFileSystem
{
    void CreateDirectory(string path);
    bool FileExists(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string contents);
    void CopyFile(string sourcePath, string destinationPath, bool overwrite);
    void DeleteDirectory(string path, bool recursive);
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
    Task<bool> ExistsAsync(CancellationToken cancellationToken);
    Task<bool> IsRunningAsync(CancellationToken cancellationToken);
    Task UninstallIfCreatedAsync(CancellationToken cancellationToken);
}
