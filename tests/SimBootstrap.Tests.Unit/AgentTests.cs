using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
}
