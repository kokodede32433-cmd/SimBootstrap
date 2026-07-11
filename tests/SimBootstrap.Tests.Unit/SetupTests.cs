using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
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
        public List<string> PairingCodes { get; } = new();
        public string? ResponseMachineCredential { get; init; } = "machine-credential-secret";
        public bool ReturnMalformedResponse { get; init; }
        public Func<string, Exception>? FailureFactory { get; init; }

        public Task<PairingResponse> PairAsync(
            string pairingCode,
            string controlServerUrl,
            string machineId,
            string machineName,
            string agentVersion,
            CancellationToken cancellationToken)
        {
            PairingCodes.Add(pairingCode);
            if (FailureFactory is not null)
            {
                throw FailureFactory(pairingCode);
            }

            if (pairingCode == "INVALID_CODE")
            {
                throw new InvalidOperationException("Pairing code is invalid.");
            }
            if (pairingCode == "EXPIRED_CODE")
            {
                throw new InvalidOperationException("Pairing code has expired.");
            }
            if (pairingCode == "REUSED_CODE")
            {
                throw new InvalidOperationException("Pairing code has already been used.");
            }
            if (pairingCode == "RATE_LIMIT_CODE")
            {
                throw new InvalidOperationException("Too many pairing attempts. Please try again later.");
            }
            if (ReturnMalformedResponse)
            {
                return Task.FromResult(new PairingResponse(string.Empty, string.Empty, string.Empty, string.Empty));
            }

            return Task.FromResult(new PairingResponse(
                "agent-id-123",
                "club-id-123",
                "location-id-123",
                ResponseMachineCredential ?? string.Empty
            ));
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
        public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
        {
            if (!overwrite && Files.ContainsKey(destinationPath))
            {
                throw new InvalidOperationException("destination exists");
            }

            Files[destinationPath] = Files[sourcePath];
            Files.Remove(sourcePath);
        }

        public void DeleteFile(string path) => Files.Remove(path);
        public void DeleteDirectory(string path, bool recursive) => DeletedDirectories.Add(path);
    }

    private sealed class FakePayloadExtractor : IAgentPayloadExtractor
    {
        public bool Extracted { get; private set; }
        public bool WriteExecutable { get; init; } = true;

        public Task ExtractAsync(string destinationDirectory, ISetupFileSystem fileSystem, CancellationToken cancellationToken)
        {
            Extracted = true;
            fileSystem.CreateDirectory(destinationDirectory);
            if (WriteExecutable)
            {
                fileSystem.WriteAllText(System.IO.Path.Combine(destinationDirectory, "SimBootstrap.Agent.exe"), "agent");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeProvisioner : ISetupProvisioner
    {
        public bool Called { get; private set; }
        public bool Fail { get; init; }
        public Task ProvisionAsync(CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("provisioning failed");
            }

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

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public string? RequestBody { get; private set; }

        public RecordingHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _respond = respond;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return _respond(request);
        }
    }

    private sealed class FakeAgentConfigAcl : IAgentConfigAcl
    {
        public List<string> AppliedPaths { get; } = new();
        public bool Fail { get; init; }

        public Task ApplyRestrictedAclAsync(string filePath, CancellationToken cancellationToken)
        {
            AppliedPaths.Add(filePath);
            if (Fail)
            {
                throw new InvalidOperationException("Failed to restrict agent settings ACL.");
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Setup_NonAdmin_RequestsElevation()
    {
        var platform = new FakePlatform { IsAdministrator = false };
        var setup = CreateSetup(platform: platform);

        var result = await setup.RunAsync(new SetupOptions("PAIR-VALID"));

        Assert.True(result.Success);
        Assert.True(platform.ElevationRequested);
        Assert.Equal("Elevation requested.", result.Message);
    }

    [Fact]
    public async Task Setup_InvalidPairingCode_FailsPairing()
    {
        var setup = CreateSetup();

        var result = await setup.RunAsync(new SetupOptions("INVALID_CODE"));

        Assert.False(result.Success);
        Assert.Equal("Pairing code is invalid.", result.Message);
    }

    [Fact]
    public async Task Setup_CreatesDirectoriesExtractsPayloadInstallsStartsAndWritesResult()
    {
        var fileSystem = new FakeFileSystem();
        var payload = new FakePayloadExtractor();
        var service = new FakeServiceManager();
        var paths = TestPaths();
        var setup = CreateSetup(fileSystem: fileSystem, payload: payload, service: service, paths: paths);

        var result = await setup.RunAsync(new SetupOptions("PAIR-VALID"));

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
    public async Task Setup_WritesCanonicalAgentSettings()
    {
        var fileSystem = new FakeFileSystem();
        var paths = TestPaths();
        var setup = CreateSetup(fileSystem: fileSystem, paths: paths);

        var result = await setup.RunAsync(new SetupOptions("PAIR-VALID"));

        Assert.True(result.Success);
        var settings = fileSystem.ReadAllText(paths.AgentSettingsPath);
        Assert.Contains("\"AgentId\": \"agent-id-123\"", settings);
        Assert.Contains("\"ClubId\": \"club-id-123\"", settings);
        Assert.Contains("\"LocationId\": \"location-id-123\"", settings);
        Assert.Contains("\"MachineCredential\": \"machine-credential-secret\"", settings);
        Assert.Contains("\"ControlServerUrl\": \"https://control.example.test/pair\"", settings);
        Assert.DoesNotContain("PairingCode", settings);
        Assert.False(fileSystem.FileExists(paths.AgentSettingsPath + ".tmp"));
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

        var result = await setup.RunAsync(new SetupOptions("PAIR-VALID"));

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

        var result = await setup.RunAsync(new SetupOptions("PAIR-VALID"));

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

        var result = await setup.RunAsync(new SetupOptions("PAIR-VALID"));

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

        var result = await setup.RunAsync(new SetupOptions("PAIR-VALID"));

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

        var result = await setup.RunAsync(new SetupOptions("PAIR-VALID"));

        Assert.False(result.Success);
        Assert.Contains("service create failed", result.Message);
        Assert.Contains(paths.ProgramFilesAgentDirectory, fileSystem.DeletedDirectories);
        Assert.True(service.Uninstalled);
    }

    [Fact]
    public async Task Setup_NonWindowsGuard_Fails()
    {
        var setup = CreateSetup(platform: new FakePlatform { IsWindows = false });

        var result = await setup.RunAsync(new SetupOptions("PAIR-VALID"));

        Assert.False(result.Success);
        Assert.Equal("SimBootstrap Setup can only run on Windows.", result.Message);
    }

    [Theory]
    [InlineData("EXPIRED_CODE", "Pairing code has expired.")]
    [InlineData("REUSED_CODE", "Pairing code has already been used.")]
    [InlineData("RATE_LIMIT_CODE", "Too many pairing attempts. Please try again later.")]
    public async Task Setup_PairingFailures_ReturnControlledMessages(string pairingCode, string expectedMessage)
    {
        var result = await CreateSetup().RunAsync(new SetupOptions(pairingCode));

        Assert.False(result.Success);
        Assert.Equal(expectedMessage, result.Message);
    }

    [Fact]
    public async Task Setup_DoesNotConsumePairingCodeBeforeProvisioningAndPayloadValidation()
    {
        var pairingClient = new FakePairingClient();
        var provisioner = new FakeProvisioner { Fail = true };

        var result = await CreateSetup(pairingClient: pairingClient, provisioner: provisioner)
            .RunAsync(new SetupOptions("PAIR-VALID"));

        Assert.False(result.Success);
        Assert.Empty(pairingClient.PairingCodes);

        pairingClient = new FakePairingClient();
        var payload = new FakePayloadExtractor { WriteExecutable = false };

        result = await CreateSetup(pairingClient: pairingClient, payload: payload)
            .RunAsync(new SetupOptions("PAIR-VALID"));

        Assert.False(result.Success);
        Assert.Empty(pairingClient.PairingCodes);
        Assert.Contains("Agent payload validation failed", result.Message);
    }

    [Fact]
    public async Task Setup_AclFailure_FailsSafelyWithoutFinalConfig()
    {
        var fileSystem = new FakeFileSystem();
        var paths = TestPaths();
        var acl = new FakeAgentConfigAcl { Fail = true };

        var result = await CreateSetup(fileSystem: fileSystem, agentConfigAcl: acl, paths: paths)
            .RunAsync(new SetupOptions("PAIR-VALID"));

        Assert.False(result.Success);
        Assert.Equal("Failed to restrict agent settings ACL.", result.Message);
        Assert.False(fileSystem.FileExists(paths.AgentSettingsPath));
        Assert.False(fileSystem.FileExists(paths.AgentSettingsPath + ".tmp"));
        Assert.Single(acl.AppliedPaths);
        Assert.EndsWith("agentsettings.json.tmp", acl.AppliedPaths[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Setup_RedactsPairingCodeAndMachineCredentialFromResults()
    {
        var fileSystem = new FakeFileSystem();
        var paths = TestPaths();
        var pairingClient = new FakePairingClient
        {
            FailureFactory = pairingCode => new InvalidOperationException($"pair {pairingCode} rejected")
        };

        var result = await CreateSetup(fileSystem: fileSystem, pairingClient: pairingClient, paths: paths)
            .RunAsync(new SetupOptions("PAIR-SECRET"));

        Assert.False(result.Success);
        Assert.DoesNotContain("PAIR-SECRET", result.Message);
        var installationResult = fileSystem.ReadAllText(paths.InstallationResultPath);
        Assert.DoesNotContain("PAIR-SECRET", installationResult);
        Assert.DoesNotContain("PAIR-SECRET", fileSystem.ReadAllText(paths.SetupLogPath));
    }

    [Theory]
    [InlineData("", "setupConfig.controlServerUrl is required.")]
    [InlineData("relative/path", "setupConfig.controlServerUrl must be an absolute URI.")]
    [InlineData("http://control.example.test/pair", "setupConfig.controlServerUrl must use HTTPS outside local development.")]
    public async Task SetupConfig_InvalidEndpoint_FailsValidation(string controlServerUrl, string expectedMessage)
    {
        var fileSystem = new FakeFileSystem();
        var paths = TestPaths();
        fileSystem.WriteAllText(paths.SetupConfigPath, $$"""
{
  "controlServerUrl": "{{controlServerUrl}}"
}
""");

        var result = await CreateSetup(fileSystem: fileSystem, paths: paths)
            .RunAsync(new SetupOptions("PAIR-VALID"));

        Assert.False(result.Success);
        Assert.Equal(expectedMessage, result.Message);
    }

    [Fact]
    public async Task SetupConfig_LocalHttpEndpoint_IsAllowedForDevelopment()
    {
        var fileSystem = new FakeFileSystem();
        var paths = TestPaths();
        fileSystem.WriteAllText(paths.SetupConfigPath, """
{
  "controlServerUrl": "http://localhost:54321/functions/v1/pair-agent"
}
""");

        var result = await CreateSetup(fileSystem: fileSystem, paths: paths)
            .RunAsync(new SetupOptions("PAIR-VALID"));

        Assert.True(result.Success);
    }

    [Fact]
    public async Task RealPairingClient_Success_SendsPairingRequestAndReturnsCredential()
    {
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
{
  "agentId": "agent-real",
  "clubId": "club-real",
  "locationId": "location-real",
  "machineCredential": "credential-real"
}
""", Encoding.UTF8, "application/json")
        });
        var client = new RealPairingClient(new HttpClient(handler));

        var response = await client.PairAsync("PAIR-REAL", "https://control.example.test/pair", "machine-id", "machine-name", "1.2.3", CancellationToken.None);

        Assert.Equal("agent-real", response.AgentId);
        Assert.Equal("club-real", response.ClubId);
        Assert.Equal("location-real", response.LocationId);
        Assert.Equal("credential-real", response.MachineCredential);
        Assert.Contains("\"pairCode\":\"PAIR-REAL\"", handler.RequestBody);
        Assert.Contains("\"machineId\":\"machine-id\"", handler.RequestBody);
    }

    [Theory]
    [InlineData("INVALID_PAIR_CODE", "Pairing code is invalid.")]
    [InlineData("PAIR_CODE_EXPIRED", "Pairing code has expired.")]
    [InlineData("PAIR_CODE_ALREADY_USED", "Pairing code has already been used.")]
    [InlineData("PAIRING_RATE_LIMITED", "Too many pairing attempts. Please try again later.")]
    [InlineData("PAIRING_UNAVAILABLE", "Pairing service is currently unavailable.")]
    public async Task RealPairingClient_ErrorCodes_MapToSafeMessages(string errorCode, string expectedMessage)
    {
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent($$"""
{
  "error": {
    "code": "{{errorCode}}",
    "message": "server message containing PAIR-SECRET"
  }
}
""", Encoding.UTF8, "application/json")
        });
        var client = new RealPairingClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.PairAsync("PAIR-SECRET", "https://control.example.test/pair", "machine-id", "machine-name", "1.2.3", CancellationToken.None));

        Assert.Equal(expectedMessage, ex.Message);
        Assert.DoesNotContain("PAIR-SECRET", ex.Message);
    }

    [Fact]
    public async Task RealPairingClient_UnavailableEndpoint_ReturnsSafeNetworkFailure()
    {
        var handler = new RecordingHttpHandler(_ => throw new HttpRequestException("low-level failure PAIR-SECRET"));
        var client = new RealPairingClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.PairAsync("PAIR-SECRET", "https://control.example.test/pair", "machine-id", "machine-name", "1.2.3", CancellationToken.None));

        Assert.Equal("Network connection failed. Please verify internet connectivity and try again.", ex.Message);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{ \"agentId\": \"agent\", \"clubId\": \"club\", \"locationId\": \"loc\" }")]
    [InlineData("{ \"agentId\": \"agent\", \"machineCredential\": \"credential\", \"locationId\": \"loc\" }")]
    [InlineData("{ \"agentId\": \"agent\", \"machineCredential\": \"credential\", \"clubId\": \"club\" }")]
    public async Task RealPairingClient_MalformedOrMissingResponseFields_FailsSafely(string body)
    {
        var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        var client = new RealPairingClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.PairAsync("PAIR-SECRET", "https://control.example.test/pair", "machine-id", "machine-name", "1.2.3", CancellationToken.None));

        Assert.Equal("Failed to parse registration credentials from the pairing service.", ex.Message);
        Assert.DoesNotContain("PAIR-SECRET", ex.Message);
    }

    private static SetupOrchestrator CreateSetup(
        FakePlatform? platform = null,
        FakePairingClient? pairingClient = null,
        FakeFileSystem? fileSystem = null,
        FakePayloadExtractor? payload = null,
        FakeProvisioner? provisioner = null,
        FakeServiceManager? service = null,
        FakeAgentConfigAcl? agentConfigAcl = null,
        SetupPaths? paths = null)
    {
        var actualPaths = paths ?? TestPaths();
        var actualFileSystem = fileSystem ?? new FakeFileSystem();
        if (!actualFileSystem.FileExists(actualPaths.SetupConfigPath))
        {
            actualFileSystem.WriteAllText(actualPaths.SetupConfigPath, """
{
  "controlServerUrl": "https://control.example.test/pair"
}
""");
        }

        return new SetupOrchestrator(
            platform ?? new FakePlatform(),
            pairingClient ?? new FakePairingClient(),
            actualFileSystem,
            payload ?? new FakePayloadExtractor(),
            provisioner ?? new FakeProvisioner(),
            service ?? new FakeServiceManager(),
            agentConfigAcl ?? new FakeAgentConfigAcl(),
            actualPaths);
    }

    private static SetupPaths TestPaths() => new()
    {
        ProgramFilesAgentDirectory = @"C:\Test\Agent",
        ProgramDataConfigDirectory = @"C:\Test\Config",
        ProgramDataLogsDirectory = @"C:\Test\Logs",
        ProgramDataStateDirectory = @"C:\Test\State"
    };
}
