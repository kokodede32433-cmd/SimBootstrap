using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine.Agent;

public interface IAgentRegistrationClient
{
    Task<AgentRegistrationResult> RegisterAgentAsync(AgentRegistrationRequest request, CancellationToken cancellationToken = default);
}

public interface IControlServerClient
{
    Task<PollResponse> PollTasksAsync(PollRequest request, CancellationToken cancellationToken = default);
    Task<bool> AckTaskAsync(TaskAckRequest request, CancellationToken cancellationToken = default);
    Task<bool> SubmitTaskResultAsync(TaskResultRequest request, CancellationToken cancellationToken = default);
    Task<bool> SendHeartbeatAsync(AgentHeartbeat heartbeat, CancellationToken cancellationToken = default);
}

public interface ITaskPoller
{
    Task StartPollingAsync(AgentId agentId, MachineId machineId, CancellationToken cancellationToken = default);
}

public interface ITaskExecutor
{
    Task<BootstrapTaskResult> ExecuteTaskAsync(BootstrapTask task, CancellationToken cancellationToken = default);
}
