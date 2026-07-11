using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Agent;
using SimBootstrap.Setup;
using Xunit;

namespace SimBootstrap.Tests.Unit;

public class SetupTests
{
    private sealed class FakePlatform : ISetupPlatform
    {
        public bool IsWindows { get; init; } = true;
        public bool IsAdministrator { get; init; } = true;
        public string ExecutablePath => @"C:\Setup\SimBootstrap.Setup.exe";
        public bool ElevationRequested { get; private set; }

        public Task<bool> RelaunchElevatedAsync(string arguments, CancellationToken cancellationToken)
        {
            ElevationRequested = true;
            return Task.FromResult(true);
        }
    }

    private sealed class FakePairingClient : IPairingClient
    {
        public List<string> PairCodes { get; } = new();

        public Task ValidatePairCodeAsync(string pairCode, CancellationToken cancellationToken)
        {
            PairCodes.Add(pairCode);
            if (pairCode != "LOCAL-PAIR-CODE")
            {
                throw new InvalidOperationException("Preview builds only accept LOCAL-PAIR-CODE.");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFileSystem : ISetupFileSystem
    {
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> DeletedDirectories { get; } = new();

        public void CreateDirectory(string path) => Directories.Add(path);
        public bool FileExists(string path) => Files.ContainsKey(path);
        public string ReadAllText(string path) => Files[path];
        public void WriteAllText(string path, string contents) => Files[path] = contents;
        public void CopyFile(string sourcePath, string destinationPath, bool overwrite) => Files[destinationPath] = Files.GetValueOrDefault(sourcePath, string.Empty);
        public void DeleteDirectory(string path, bool recursive) => DeletedDirectories.Add(path);
    }

    private sealed class FakePayloadExtractor : IAgentPayloadExtractor
    {
        public bool Extracted { get; private set; }

        public Task ExtractAsync(string destinationDirectory, ISetupFileSystem fileSystem, CancellationToken cancellationToken)
        {
            Extracted = true;
            fileSystem.CreateDirectory(destinationDirectory);
            fileSystem.WriteAllText(System.IO.Path.Combine(destinationDirectory, "SimBootstrap.Agent.exe"), "agent");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProvisioner : ISetupProvisioner
    {
        public bool Called { get; private set; }
        public Task ProvisionAsync(CancellationToken cancellationToken)
        {
            Called = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeServiceManager : ISetupServiceManager
    {
        public bool Installed { get; private set; }
        public bool Started { get; private set; }
        public bool Uninstalled { get; private set; }
        public bool FailInstall { get; init; }
        public ServiceWaitResult VerificationResult { get; init; } = new(true, SetupServiceStatus.Running, TimeSpan.FromMilliseconds(1));

        public Task InstallOrUpdateAsync(string agentExePath, string configPath, CancellationToken cancellationToken)
        {
            if (FailInstall)
            {
                throw new InvalidOperationException("service create failed");
            }
            Installed = true;
            return Task.CompletedTask;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started = true;
            return Task.CompletedTask;
        }

        public Task<ServiceWaitResult> WaitForRunningAsync(TimeSpan timeout, TimeSpan retryInterval, CancellationToken cancellationToken)
            => Task.FromResult(VerificationResult);

        public Task UninstallIfCreatedAsync(CancellationToken cancellationToken)
        {
            Uninstalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStatusReader : IWindowsServiceStatusReader
    {
        private readonly Queue<SetupServiceStatus> _statuses;

        public FakeStatusReader(params SetupServiceStatus[] statuses)
        {
            _statuses = new Queue<SetupServiceStatus>(statuses);
        }

        public Task<SetupServiceStatus> GetStatusAsync(string serviceName, CancellationToken cancellationToken)
        {
            return Task.FromResult(_statuses.Count > 1 ? _statuses.Dequeue() : _statuses.Peek());
        }
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<string> Commands { get; } = new();

        public Task<CommandExecutionResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default)
        {
            Commands.Add($"{fileName} {arguments}");
            return Task.FromResult(new CommandExecutionResult(0, "Состояние: 4 RUNNING", string.Empty));
        }
    }

    [Fact]
    public async Task Setup_NonAdmin_RequestsElevation()
    {
        var platform = new FakePlatform { IsAdministrator = false };
        var setup = CreateSetup(platform: platform);

        var result = await setup.RunAsync(new SetupOptions("LOCAL-PAIR-CODE"));

        Assert.True(result.Success);
        Assert.True(platform.ElevationRequested);
        Assert.Equal("Elevation requested.", result.Message);
    }

    [Fact]
    public async Task Setup_InvalidPairCode_FailsPreviewValidation()
    {
        var setup = CreateSetup();

        var result = await setup.RunAsync(new SetupOptions("BAD-CODE"));

        Assert.False(result.Success);
        Assert.Contains("LOCAL-PAIR-CODE", result.Message);
    }

    [Fact]
    public async Task Setup_CreatesDirectoriesExtractsPayloadInstallsStartsAndWritesResult()
    {
        var fileSystem = new FakeFileSystem();
        var payload = new FakePayloadExtractor();
        var service = new FakeServiceManager();
        var paths = TestPaths();
        var setup = CreateSetup(fileSystem: fileSystem, payload: payload, service: service, paths: paths);

        var result = await setup.RunAsync(new SetupOptions("LOCAL-PAIR-CODE"));

        Assert.True(result.Success);
        Assert.Contains(paths.ProgramDataConfigDirectory, fileSystem.Directories);
        Assert.Contains(paths.ProgramDataLogsDirectory, fileSystem.Directories);
        Assert.Contains(paths.ProgramDataStateDirectory, fileSystem.Directories);
        Assert.True(payload.Extracted);
        Assert.True(service.Installed);
        Assert.True(service.Started);
        Assert.True(fileSystem.FileExists(paths.InstallationResultPath));
        Assert.Equal("Installation completed successfully.", result.Message);
        Assert.Contains("\"Success\": true", fileSystem.ReadAllText(paths.InstallationResultPath));
        Assert.Contains("\"State\": \"Completed\"", fileSystem.ReadAllText(paths.InstallationResultPath));
    }

    [Fact]
    public async Task Setup_PreservesValidExistingConfigOnRepeatedInstall()
    {
        var fileSystem = new FakeFileSystem();
        var paths = TestPaths();
        fileSystem.WriteAllText(paths.AgentSettingsPath, """
{
  "agentId": "existing-agent",
  "pairCode": "LOCAL-PAIR-CODE",
  "mockControlServerUrl": "mock://local-control",
  "heartbeatIntervalSeconds": 30
}
""");
        var setup = CreateSetup(fileSystem: fileSystem, paths: paths);

        var result = await setup.RunAsync(new SetupOptions("LOCAL-PAIR-CODE"));

        Assert.True(result.Success);
        Assert.Contains("existing-agent", fileSystem.ReadAllText(paths.AgentSettingsPath));
    }

    [Fact]
    public async Task Setup_StartPendingThenRunning_Succeeds()
    {
        var processRunner = new FakeProcessRunner();
        var statusReader = new FakeStatusReader(SetupServiceStatus.StartPending, SetupServiceStatus.Running);
        var manager = new AgentServiceSetupManager(processRunner, statusReader);

        var waitResult = await manager.WaitForRunningAsync(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), CancellationToken.None);

        Assert.True(waitResult.ReachedRunning);
        var fileSystem = new FakeFileSystem();
        var paths = TestPaths();
        var service = new FakeServiceManager
        {
            VerificationResult = new ServiceWaitResult(true, SetupServiceStatus.Running, TimeSpan.FromMilliseconds(500))
        };
        var setup = CreateSetup(fileSystem: fileSystem, service: service, paths: paths);

        var result = await setup.RunAsync(new SetupOptions("LOCAL-PAIR-CODE"));

        Assert.True(result.Success);
        Assert.Contains("\"Success\": true", fileSystem.ReadAllText(paths.InstallationResultPath));
        Assert.Contains("\"State\": \"Completed\"", fileSystem.ReadAllText(paths.InstallationResultPath));
        Assert.Contains("Installation completed successfully.", fileSystem.ReadAllText(paths.InstallationResultPath));
    }

    [Fact]
    public async Task Setup_ImmediateRunning_Succeeds()
    {
        var setup = CreateSetup(service: new FakeServiceManager
        {
            VerificationResult = new ServiceWaitResult(true, SetupServiceStatus.Running, TimeSpan.Zero)
        });

        var result = await setup.RunAsync(new SetupOptions("LOCAL-PAIR-CODE"));

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Setup_LocalizedScOutput_IsIrrelevantForRunningVerification()
    {
        var processRunner = new FakeProcessRunner();
        var statusReader = new FakeStatusReader(SetupServiceStatus.Running);
        var service = new AgentServiceSetupManager(processRunner, statusReader);

        var result = await service.WaitForRunningAsync(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), CancellationToken.None);

        Assert.True(result.ReachedRunning);
        Assert.Empty(processRunner.Commands);
    }

    [Fact]
    public async Task Setup_VerificationTimeout_ReturnsControlledFailure()
    {
        var setup = CreateSetup(service: new FakeServiceManager
        {
            VerificationResult = new ServiceWaitResult(false, SetupServiceStatus.StartPending, TimeSpan.FromSeconds(30))
        });

        var result = await setup.RunAsync(new SetupOptions("LOCAL-PAIR-CODE"));

        Assert.False(result.Success);
        Assert.Contains("did not reach Running", result.Message);
        Assert.Contains("Last observed status: StartPending", result.Message);
        Assert.Contains("Elapsed timeout: 30.0 seconds", result.Message);
    }

    [Fact]
    public async Task Setup_ServiceMissing_ReturnsControlledFailure()
    {
        var setup = CreateSetup(service: new FakeServiceManager
        {
            VerificationResult = new ServiceWaitResult(false, SetupServiceStatus.Missing, TimeSpan.FromMilliseconds(10))
        });

        var result = await setup.RunAsync(new SetupOptions("LOCAL-PAIR-CODE"));

        Assert.False(result.Success);
        Assert.Contains("did not reach Running", result.Message);
        Assert.Contains("Last observed status: Missing", result.Message);
    }

    [Fact]
    public async Task Setup_RollbackAfterFailedServiceCreation_RemovesAgentDirectory()
    {
        var fileSystem = new FakeFileSystem();
        var service = new FakeServiceManager { FailInstall = true };
        var paths = TestPaths();
        var setup = CreateSetup(fileSystem: fileSystem, service: service, paths: paths);

        var result = await setup.RunAsync(new SetupOptions("LOCAL-PAIR-CODE"));

        Assert.False(result.Success);
        Assert.Contains("service create failed", result.Message);
        Assert.Contains(paths.ProgramFilesAgentDirectory, fileSystem.DeletedDirectories);
        Assert.True(service.Uninstalled);
    }

    [Fact]
    public async Task Setup_NonWindowsGuard_Fails()
    {
        var setup = CreateSetup(platform: new FakePlatform { IsWindows = false });

        var result = await setup.RunAsync(new SetupOptions("LOCAL-PAIR-CODE"));

        Assert.False(result.Success);
        Assert.Equal("SimBootstrap Setup can only run on Windows.", result.Message);
    }

    private static SetupOrchestrator CreateSetup(
        FakePlatform? platform = null,
        FakeFileSystem? fileSystem = null,
        FakePayloadExtractor? payload = null,
        FakeProvisioner? provisioner = null,
        FakeServiceManager? service = null,
        SetupPaths? paths = null)
    {
        return new SetupOrchestrator(
            platform ?? new FakePlatform(),
            new FakePairingClient(),
            fileSystem ?? new FakeFileSystem(),
            payload ?? new FakePayloadExtractor(),
            provisioner ?? new FakeProvisioner(),
            service ?? new FakeServiceManager(),
            paths ?? TestPaths());
    }

    private static SetupPaths TestPaths() => new()
    {
        ProgramFilesAgentDirectory = @"C:\Test\Agent",
        ProgramDataConfigDirectory = @"C:\Test\Config",
        ProgramDataLogsDirectory = @"C:\Test\Logs",
        ProgramDataStateDirectory = @"C:\Test\State"
    };
}
