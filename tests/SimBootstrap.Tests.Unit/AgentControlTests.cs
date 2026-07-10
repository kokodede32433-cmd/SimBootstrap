using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using SimBootstrap.Contracts;
using SimBootstrap.Engine.Agent;

namespace SimBootstrap.Tests.Unit;

public class AgentControlTests
{
    private readonly MockControlServer _server;
    private readonly MockControlServerClient _client;
    private readonly AgentSettings _settings;
    private readonly MockTaskExecutor _taskExecutor;

    public AgentControlTests()
    {
        _server = new MockControlServer();
        _client = new MockControlServerClient(_server);
        _taskExecutor = new MockTaskExecutor();
        _settings = new AgentSettings
        {
            ControlServerUrl = "http://localhost",
            PairCode = "TEST_CODE",
            PollingIntervalSeconds = 5,
            MachineName = "TestMachine",
            DryRun = true
        };
    }

    [Fact]
    public async Task Registration_ValidPairCode_Succeeds()
    {
        var request = new AgentRegistrationRequest(
            new MachineId("mac-123"),
            "TestMachine",
            new PairCode("TEST_CODE")
        );

        var result = await _client.RegisterAgentAsync(request, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.AgentId);
        Assert.Null(result.ErrorMessage);

        var serverState = _server.GetAgentState(result.AgentId);
        Assert.NotNull(serverState);
        Assert.Equal(AgentStatus.Registered, serverState.Status);
    }

    [Fact]
    public async Task Registration_InvalidPairCode_Fails()
    {
        var request = new AgentRegistrationRequest(
            new MachineId("mac-123"),
            "TestMachine",
            new PairCode("BAD_CODE")
        );

        var result = await _client.RegisterAgentAsync(request, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.AgentId);
        Assert.Equal("Invalid pairing code.", result.ErrorMessage);
    }

    [Fact]
    public async Task Heartbeat_ValidAgent_Accepted()
    {
        // First register
        var reg = await _client.RegisterAgentAsync(new AgentRegistrationRequest(new MachineId("m1"), "t1", new PairCode("TEST_CODE")), CancellationToken.None);
        Assert.True(reg.Success);

        var heartbeat = new AgentHeartbeat(reg.AgentId!, AgentStatus.Online);
        var success = await _client.SendHeartbeatAsync(heartbeat, CancellationToken.None);

        Assert.True(success);
        var state = _server.GetAgentState(reg.AgentId!);
        Assert.Equal(AgentStatus.Online, state!.Status);
        Assert.True((DateTime.UtcNow - state.LastHeartbeatUtc).TotalSeconds < 5);
    }

    [Fact]
    public async Task Heartbeat_InvalidAgent_Rejected()
    {
        var heartbeat = new AgentHeartbeat(new AgentId("unknown-agent"), AgentStatus.Online);
        var success = await _client.SendHeartbeatAsync(heartbeat, CancellationToken.None);

        Assert.False(success);
    }

    [Fact]
    public async Task TaskFlow_PollingAckAndResult_Succeeds()
    {
        // 1. Register agent via client
        var reg = await _client.RegisterAgentAsync(new AgentRegistrationRequest(new MachineId("m1"), "t1", new PairCode("TEST_CODE")), CancellationToken.None);
        Assert.True(reg.Success);

        // 2. Queue a task on the server
        var task = new BootstrapTask
        {
            TaskId = "task-001",
            Type = BootstrapTaskType.InstallPackage,
            TargetPackageId = "git",
            Parameters = new Dictionary<string, string> { { "version", "2.40.1" } }
        };
        _server.QueueTask(reg.AgentId!, task);

        // 3. Poll tasks
        var pollRequest = new PollRequest(reg.AgentId!, new MachineId("m1"), AgentStatus.Online);
        var pollResponse = await _client.PollTasksAsync(pollRequest, CancellationToken.None);

        Assert.Single(pollResponse.PendingTasks);
        Assert.Equal("task-001", pollResponse.PendingTasks[0].TaskId);

        // 4. Ack task
        var ackSuccess = await _client.AckTaskAsync(new TaskAckRequest(reg.AgentId!, "task-001", DateTime.UtcNow), CancellationToken.None);
        Assert.True(ackSuccess);
        var ackedIds = _server.GetAckedTaskIds(reg.AgentId!);
        Assert.Contains("task-001", ackedIds);

        // 5. Submit result
        var taskResult = await _taskExecutor.ExecuteTaskAsync(task, CancellationToken.None);
        var submitSuccess = await _client.SubmitTaskResultAsync(new TaskResultRequest(reg.AgentId!, "task-001", taskResult), CancellationToken.None);
        
        Assert.True(submitSuccess);
        var results = _server.GetResults(reg.AgentId!);
        Assert.Single(results);
        Assert.Equal("task-001", results[0].TaskId);
        Assert.Equal(BootstrapTaskStatus.Succeeded, results[0].Status);
    }

    [Fact]
    public async Task AgentRuntime_ExecutionLoop_Succeeds()
    {
        var logger = NullLogger<AgentRuntime>.Instance;
        var runtime = new AgentRuntime(_client, _client, _settings, _taskExecutor, logger);

        // Queue a task on the server for when the runtime registers
        // Since we don't know the AgentId in advance, we will initialize the runtime first
        await runtime.InitializeAsync(CancellationToken.None);
        var agentId = runtime.AgentId;
        Assert.NotNull(agentId);

        var task = new BootstrapTask
        {
            TaskId = "task-002",
            Type = BootstrapTaskType.RunDiagnostics
        };
        _server.QueueTask(agentId, task);

        // Run the agent loop once
        await runtime.RunOnceAsync(CancellationToken.None);

        // Verify task was acked and completed on server
        var acked = _server.GetAckedTaskIds(agentId);
        Assert.Contains("task-002", acked);

        var results = _server.GetResults(agentId);
        Assert.Single(results);
        Assert.Equal("task-002", results[0].TaskId);
        Assert.Equal(BootstrapTaskStatus.Succeeded, results[0].Status);
    }
}
