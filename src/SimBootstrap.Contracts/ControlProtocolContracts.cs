using System;
using System.Collections.Generic;

namespace SimBootstrap.Contracts;

public record PollRequest(AgentId AgentId, MachineId MachineId, AgentStatus CurrentStatus);

public record PollResponse(List<BootstrapTask> PendingTasks);

public record TaskAckRequest(AgentId AgentId, string TaskId, DateTime AcceptedAtUtc);

public record TaskResultRequest(AgentId AgentId, string TaskId, BootstrapTaskResult Result);
