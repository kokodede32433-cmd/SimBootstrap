using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine.Agent;

public class MockTaskExecutor : ITaskExecutor
{
    public Task<BootstrapTaskResult> ExecuteTaskAsync(BootstrapTask task, CancellationToken cancellationToken = default)
    {
        var result = new BootstrapTaskResult
        {
            TaskId = task.TaskId,
            Status = BootstrapTaskStatus.Succeeded,
            CompletedAtUtc = DateTime.UtcNow
        };

        result.Logs.Add(new BootstrapTaskLogEntry(DateTime.UtcNow, $"Started executing task: {task.TaskId} of type {task.Type}"));
        result.Logs.Add(new BootstrapTaskLogEntry(DateTime.UtcNow, $"Task target package: {task.TargetPackageId ?? "None"}"));

        foreach (var param in task.Parameters)
        {
            result.Logs.Add(new BootstrapTaskLogEntry(DateTime.UtcNow, $"Parameter: {param.Key} = {param.Value}"));
        }

        result.Logs.Add(new BootstrapTaskLogEntry(DateTime.UtcNow, "Task completed successfully (Mock)."));
        return Task.FromResult(result);
    }
}

public class AgentRuntime
{
    private readonly IAgentRegistrationClient _registrationClient;
    private readonly IControlServerClient _controlClient;
    private readonly AgentSettings _settings;
    private readonly ILogger<AgentRuntime> _logger;
    private readonly ITaskExecutor _taskExecutor;

    public AgentId? AgentId { get; private set; }
    public AgentStatus Status { get; private set; } = AgentStatus.Unregistered;
    public MachineId MachineId { get; }

    public AgentRuntime(
        IAgentRegistrationClient registrationClient,
        IControlServerClient controlClient,
        AgentSettings settings,
        ITaskExecutor taskExecutor,
        ILogger<AgentRuntime> logger)
    {
        _registrationClient = registrationClient;
        _controlClient = controlClient;
        _settings = settings;
        _taskExecutor = taskExecutor;
        _logger = logger;

        // Generate a machine ID for the host
        MachineId = new MachineId("mac-" + Guid.NewGuid().ToString("n")[..8]);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing agent runtime...");
        if (AgentId != null) return;

        var request = new AgentRegistrationRequest(
            MachineId,
            string.IsNullOrWhiteSpace(_settings.MachineName) ? Environment.MachineName : _settings.MachineName,
            new PairCode(_settings.PairCode)
        );

        _logger.LogInformation("Registering agent with pair code '{PairCode}'...", _settings.PairCode);
        var result = await _registrationClient.RegisterAgentAsync(request, cancellationToken);
        if (result.Success && result.AgentId != null)
        {
            AgentId = result.AgentId;
            Status = AgentStatus.Registered;
            _logger.LogInformation("Agent registered successfully. Assigned ID: {AgentId}", AgentId);
        }
        else
        {
            Status = AgentStatus.Error;
            _logger.LogError("Agent registration failed: {ErrorMessage}", result.ErrorMessage);
            throw new InvalidOperationException($"Registration failed: {result.ErrorMessage}");
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (AgentId == null)
        {
            await InitializeAsync(cancellationToken);
        }

        // Send Heartbeat
        Status = AgentStatus.Online;
        _logger.LogInformation("Sending heartbeat for agent {AgentId}...", AgentId);
        var heartbeatResult = await _controlClient.SendHeartbeatAsync(new AgentHeartbeat(AgentId!, Status), cancellationToken);
        if (!heartbeatResult)
        {
            _logger.LogWarning("Heartbeat rejected by server.");
        }

        // Poll for tasks
        _logger.LogInformation("Polling for pending tasks...");
        var pollRequest = new PollRequest(AgentId!, MachineId, Status);
        var pollResponse = await _controlClient.PollTasksAsync(pollRequest, cancellationToken);

        if (pollResponse.PendingTasks.Count > 0)
        {
            _logger.LogInformation("Received {Count} tasks.", pollResponse.PendingTasks.Count);
            foreach (var task in pollResponse.PendingTasks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Ack task
                _logger.LogInformation("Acknowledging task {TaskId}...", task.TaskId);
                await _controlClient.AckTaskAsync(new TaskAckRequest(AgentId!, task.TaskId, DateTime.UtcNow), cancellationToken);

                // Execute task
                _logger.LogInformation("Executing task {TaskId} ({Type})...", task.TaskId, task.Type);
                var result = await _taskExecutor.ExecuteTaskAsync(task, cancellationToken);

                // Submit result
                _logger.LogInformation("Submitting result for task {TaskId} (Status: {Status})...", task.TaskId, result.Status);
                await _controlClient.SubmitTaskResultAsync(new TaskResultRequest(AgentId!, task.TaskId, result), cancellationToken);
            }
        }
        else
        {
            _logger.LogInformation("No pending tasks returned.");
        }
    }
}
