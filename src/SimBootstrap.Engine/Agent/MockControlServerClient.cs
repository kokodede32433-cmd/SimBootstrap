using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine.Agent;

public class MockControlServerClient : IAgentRegistrationClient, IControlServerClient
{
    private readonly MockControlServer _server;

    public MockControlServerClient(MockControlServer server)
    {
        _server = server;
    }

    public Task<AgentRegistrationResult> RegisterAgentAsync(AgentRegistrationRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_server.Register(request));
    }

    public Task<PollResponse> PollTasksAsync(PollRequest request, CancellationToken cancellationToken = default)
    {
        var tasks = _server.Poll(request);
        return Task.FromResult(new PollResponse(tasks));
    }

    public Task<bool> AckTaskAsync(TaskAckRequest request, CancellationToken cancellationToken = default)
    {
        var result = _server.AckTask(request.AgentId, request.TaskId);
        return Task.FromResult(result);
    }

    public Task<bool> SubmitTaskResultAsync(TaskResultRequest request, CancellationToken cancellationToken = default)
    {
        var result = _server.SubmitResult(request.AgentId, request.TaskId, request.Result);
        return Task.FromResult(result);
    }

    public Task<bool> SendHeartbeatAsync(AgentHeartbeat heartbeat, CancellationToken cancellationToken = default)
    {
        var result = _server.Heartbeat(heartbeat);
        return Task.FromResult(result);
    }
}
