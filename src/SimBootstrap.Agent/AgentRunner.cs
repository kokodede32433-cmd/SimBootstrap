using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SimBootstrap.Agent;

public sealed class AgentRunner
{
    private readonly IMockControlServerClient _controlServer;
    private readonly IAgentLogWriter _logWriter;
    private string? _registrationId;

    public AgentRunner(IMockControlServerClient controlServer, IAgentLogWriter logWriter)
    {
        _controlServer = controlServer;
        _logWriter = logWriter;
    }

    public async Task<AgentRunResult> RunOnceAsync(AgentSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        var events = new List<string>();

        async Task RecordAsync(string message)
        {
            events.Add(message);
            await _logWriter.WriteAsync(message, cancellationToken);
        }

        await RecordAsync($"Agent '{settings.AgentId}' starting run-once.");
        if (string.IsNullOrWhiteSpace(_registrationId))
        {
            _registrationId = await _controlServer.RegisterAsync(settings, cancellationToken);
            await RecordAsync("Registered with mock control server.");
        }
        else
        {
            await RecordAsync("Using existing mock control server registration.");
        }

        await _controlServer.SendHeartbeatAsync(_registrationId, cancellationToken);
        await RecordAsync("Heartbeat sent.");

        var task = await _controlServer.PollTaskAsync(_registrationId, cancellationToken);
        if (task is null)
        {
            await RecordAsync("No mock task available.");
            return new AgentRunResult(true, settings.AgentId, _registrationId, string.Empty, events);
        }

        await RecordAsync($"Polled mock task '{task.TaskId}'.");
        var taskResult = ExecuteMockTask(task);
        await RecordAsync($"Executed mock task '{task.TaskId}': {taskResult.Message}");

        await _controlServer.ReportResultAsync(_registrationId, taskResult, cancellationToken);
        await RecordAsync($"Reported result for mock task '{task.TaskId}'.");

        return new AgentRunResult(taskResult.Success, settings.AgentId, _registrationId, task.TaskId, events);
    }

    private static MockTaskResult ExecuteMockTask(MockAgentTask task)
    {
        return task.Kind switch
        {
            MockAgentTaskKind.NoOp => new MockTaskResult(task.TaskId, true, "No-op task completed."),
            MockAgentTaskKind.Echo => new MockTaskResult(task.TaskId, true, task.Payload),
            _ => new MockTaskResult(task.TaskId, false, $"Unsupported mock task kind: {task.Kind}")
        };
    }
}
