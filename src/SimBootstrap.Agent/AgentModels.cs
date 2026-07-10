using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SimBootstrap.Agent;

public enum MockAgentTaskKind
{
    NoOp,
    Echo
}

public sealed record MockAgentTask(string TaskId, MockAgentTaskKind Kind, string Payload);

public sealed record MockTaskResult(string TaskId, bool Success, string Message);

public sealed record AgentRunResult(
    bool Success,
    string AgentId,
    string RegistrationId,
    string TaskId,
    IReadOnlyList<string> Events);

public interface IMockControlServerClient
{
    Task<string> RegisterAsync(AgentSettings settings, CancellationToken cancellationToken);
    Task SendHeartbeatAsync(string registrationId, CancellationToken cancellationToken);
    Task<MockAgentTask?> PollTaskAsync(string registrationId, CancellationToken cancellationToken);
    Task ReportResultAsync(string registrationId, MockTaskResult result, CancellationToken cancellationToken);
}

public sealed class MockControlServerClient : IMockControlServerClient
{
    public Task<string> RegisterAsync(AgentSettings settings, CancellationToken cancellationToken)
    {
        var registrationId = $"mock-registration-{settings.PairCode.Trim()}";
        return Task.FromResult(registrationId);
    }

    public Task SendHeartbeatAsync(string registrationId, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task<MockAgentTask?> PollTaskAsync(string registrationId, CancellationToken cancellationToken)
    {
        return Task.FromResult<MockAgentTask?>(new MockAgentTask("mock-task-001", MockAgentTaskKind.Echo, "SimBootstrap mock task executed."));
    }

    public Task ReportResultAsync(string registrationId, MockTaskResult result, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
