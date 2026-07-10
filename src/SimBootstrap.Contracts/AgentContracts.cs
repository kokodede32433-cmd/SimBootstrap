using System;

namespace SimBootstrap.Contracts;

public record AgentId(string Value)
{
    public override string ToString() => Value;
}

public record MachineId(string Value)
{
    public override string ToString() => Value;
}

public record ClubId(string Value)
{
    public override string ToString() => Value;
}

public record LocationId(string Value)
{
    public override string ToString() => Value;
}

public record PairCode(string Value)
{
    public override string ToString() => Value;
}

public enum AgentStatus
{
    Unregistered,
    Registered,
    Online,
    Offline,
    Error
}

public record AgentRegistrationRequest(MachineId MachineId, string MachineName, PairCode PairCode);

public record AgentRegistrationResult(bool Success, AgentId? AgentId, string? ErrorMessage);

public record AgentHeartbeat(AgentId AgentId, AgentStatus CurrentStatus);
