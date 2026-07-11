using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using SimBootstrap.Agent;
using Xunit;

namespace SimBootstrap.Tests.Unit;

public class AgentTests
{
    private sealed class RecordingControlServerClient : IMockControlServerClient
    {
        private readonly MockAgentTask? _task;

        public List<string> Calls { get; } = new();
        public MockTaskResult? LastResult { get; private set; }

        public RecordingControlServerClient(MockAgentTask? task)
        {
            _task = task;
        }

        public Task<string> RegisterAsync(AgentSettings settings, CancellationToken cancellationToken)
        {
            Calls.Add($"register:{settings.PairCode}");
            return Task.FromResult("registration-123");
        }

        public Task SendHeartbeatAsync(string registrationId, CancellationToken cancellationToken)
        {
            Calls.Add($"heartbeat:{registrationId}");
            return Task.CompletedTask;
        }

        public Task<MockAgentTask?> PollTaskAsync(string registrationId, CancellationToken cancellationToken)
        {
            Calls.Add($"poll:{registrationId}");
            return Task.FromResult(_task);
        }

        public Task ReportResultAsync(string registrationId, MockTaskResult result, CancellationToken cancellationToken)
        {
            Calls.Add($"report:{registrationId}:{result.TaskId}:{result.Success}");
            LastResult = result;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        private readonly Queue<CommandExecutionResult> _results;

        public List<string> Commands { get; } = new();

        public RecordingProcessRunner(params CommandExecutionResult[] results)
        {
            _results = new Queue<CommandExecutionResult>(results);
        }

        public Task<CommandExecutionResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default)
        {
            Commands.Add($"{fileName} {arguments}");
            if (_results.Count > 0)
            {
                return Task.FromResult(_results.Dequeue());
            }

            return Task.FromResult(new CommandExecutionResult(0, string.Empty, string.Empty));
        }
    }

    [Fact]
    public async Task AgentRunner_RunOnce_CompletesMockLifecycle()
    {
        var settings = new AgentSettings
        {
            AgentId = "agent-001",
            PairCode = "PAIR-001",
            MockControlServerUrl = "mock://local-control",
            HeartbeatIntervalSeconds = 30
        };
        var server = new RecordingControlServerClient(new MockAgentTask("task-001", MockAgentTaskKind.Echo, "hello from task"));
        var logs = new InMemoryAgentLogWriter();
        var runner = new AgentRunner(server, logs);

        var result = await runner.RunOnceAsync(settings);

        Assert.True(result.Success);
        Assert.Equal("agent-001", result.AgentId);
        Assert.Equal("registration-123", result.RegistrationId);
        Assert.Equal("task-001", result.TaskId);
        Assert.Equal(new[]
        {
            "register:PAIR-001",
            "heartbeat:registration-123",
            "poll:registration-123",
            "report:registration-123:task-001:True"
        }, server.Calls);
        Assert.NotNull(server.LastResult);
        Assert.Equal("hello from task", server.LastResult.Message);
        Assert.Contains(logs.Messages, message => message.Contains("Heartbeat sent.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentRunner_RunOnce_WithNoTask_ReturnsSuccessWithoutReport()
    {
        var settings = new AgentSettings
        {
            AgentId = "agent-002",
            PairCode = "PAIR-002"
        };
        var server = new RecordingControlServerClient(task: null);
        var runner = new AgentRunner(server, new InMemoryAgentLogWriter());

        var result = await runner.RunOnceAsync(settings);

        Assert.True(result.Success);
        Assert.Equal(string.Empty, result.TaskId);
        Assert.DoesNotContain(server.Calls, call => call.StartsWith("report:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AgentSettingsLoader_ValidConfig_LoadsSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentsettings-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
{
  "agentId": "agent-003",
  "pairCode": "PAIR-003",
  "mockControlServerUrl": "mock://local-control",
  "heartbeatIntervalSeconds": 15
}
""");

        try
        {
            var settings = await AgentSettingsLoader.LoadAsync(path);

            Assert.Equal("agent-003", settings.AgentId);
            Assert.Equal("PAIR-003", settings.PairCode);
            Assert.Equal(15, settings.HeartbeatIntervalSeconds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AgentSettingsLoader_MissingPairCode_FailsValidation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentsettings-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
{
  "agentId": "agent-004",
  "mockControlServerUrl": "mock://local-control",
  "heartbeatIntervalSeconds": 15
}
""");

        try
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => AgentSettingsLoader.LoadAsync(path));
            Assert.Equal("agentSettings.pairCode is required.", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("install-service", AgentServiceCommandKind.Install)]
    [InlineData("uninstall-service", AgentServiceCommandKind.Uninstall)]
    [InlineData("start-service", AgentServiceCommandKind.Start)]
    [InlineData("stop-service", AgentServiceCommandKind.Stop)]
    [InlineData("service-status", AgentServiceCommandKind.Status)]
    public void AgentProgram_ServiceCommandParsing_MapsKnownCommands(string command, AgentServiceCommandKind expected)
    {
        var mapped = SimBootstrap.Agent.Program.TryMapServiceCommand(command, out var actual);

        Assert.True(mapped);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void WindowsServiceCommandBuilder_InstallPlan_ConfiguresServiceRecovery()
    {
        var plan = WindowsServiceCommandBuilder.BuildInstallPlan(
            @"C:\Program Files\SimBootstrap\SimBootstrap.Agent.exe",
            @"C:\ProgramData\SimBootstrap\config\agentsettings.json");

        Assert.Contains(plan.Commands, command => command.Contains("sc.exe create SimBootstrapAgent", StringComparison.Ordinal));
        Assert.Contains(plan.Commands, command => command.Contains("start= delayed-auto", StringComparison.Ordinal));
        Assert.Contains(plan.Commands, command => command.Contains("DisplayName= \"SimBootstrap Agent\"", StringComparison.Ordinal));
        Assert.Contains(plan.Commands, command => command.Contains("failure SimBootstrapAgent reset= 86400 actions= restart/60000/restart/60000/restart/60000", StringComparison.Ordinal));
        Assert.Contains(plan.Commands, command => command.Contains("failureflag SimBootstrapAgent 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WindowsServiceManager_UninstallMissingService_IsIdempotent()
    {
        var processRunner = new RecordingProcessRunner(new CommandExecutionResult(1060, string.Empty, "missing service"));
        var manager = new WindowsServiceManager(processRunner, isWindows: () => true);

        var result = await manager.ExecuteAsync(
            AgentServiceCommandKind.Uninstall,
            @"C:\Program Files\SimBootstrap\SimBootstrap.Agent.exe",
            @"C:\ProgramData\SimBootstrap\config\agentsettings.json");

        Assert.True(result.Success);
        Assert.Equal("Service is not installed.", result.Message);
        Assert.Single(processRunner.Commands);
        Assert.Equal("sc.exe query SimBootstrapAgent", processRunner.Commands[0]);
    }

    [Fact]
    public async Task WindowsServiceManager_InstallExistingService_IsIdempotent()
    {
        var processRunner = new RecordingProcessRunner(new CommandExecutionResult(0, "service exists", string.Empty));
        var manager = new WindowsServiceManager(processRunner, isWindows: () => true);

        var result = await manager.ExecuteAsync(
            AgentServiceCommandKind.Install,
            @"C:\Program Files\SimBootstrap\SimBootstrap.Agent.exe",
            @"C:\ProgramData\SimBootstrap\config\agentsettings.json");

        Assert.True(result.Success);
        Assert.Equal("Service already installed.", result.Message);
        Assert.Single(processRunner.Commands);
        Assert.Equal("sc.exe query SimBootstrapAgent", processRunner.Commands[0]);
    }


    [Fact]
    public async Task WindowsServiceManager_NonWindows_ReturnsGuardFailure()
    {
        var manager = new WindowsServiceManager(new RecordingProcessRunner(), isWindows: () => false);

        var result = await manager.ExecuteAsync(
            AgentServiceCommandKind.Start,
            "/tmp/SimBootstrap.Agent",
            "/tmp/agentsettings.json");

        Assert.False(result.Success);
        Assert.Equal("Windows service commands can only run on Windows.", result.Message);
        Assert.Empty(result.Commands);
    }

    [Fact]
    public void AgentCommandLine_DefaultWindowsConfigPath_UsesProgramDataWhenWindows()
    {
        var explicitPath = @"C:\ProgramData\SimBootstrap\config\agentsettings.json";

        var options = AgentCommandLine.Parse(new[] { "service-status", "--config", explicitPath }, @"C:\agent\SimBootstrap.Agent.exe");

        Assert.Equal("service-status", options.Command);
        Assert.Equal(explicitPath, options.ConfigPath);
        Assert.EndsWith("SimBootstrap.Agent.exe", options.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentWorker_StopAsync_CancelsGracefully()
    {
        var settings = new AgentSettings
        {
            AgentId = "agent-service",
            PairCode = "PAIR-SERVICE",
            HeartbeatIntervalSeconds = 60
        };
        var server = new RecordingControlServerClient(task: null);
        var logs = new InMemoryAgentLogWriter();
        var runner = new AgentRunner(server, logs);
        var worker = new AgentWorker(runner, Options.Create(new AgentServiceOptions { Settings = settings }), logs);

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);

        Assert.Contains(logs.Messages, message => message == "SimBootstrap Agent service loop started.");
        Assert.Contains(logs.Messages, message => message == "SimBootstrap Agent service loop stopped.");
    }
}
