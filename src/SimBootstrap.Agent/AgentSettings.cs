using System;

namespace SimBootstrap.Agent;

public class AgentSettings
{
    public string AgentId { get; set; } = string.Empty;
    public string PairCode { get; set; } = string.Empty;
    public string MockControlServerUrl { get; set; } = "mock://local-control";
    public int HeartbeatIntervalSeconds { get; set; } = 60;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AgentId))
        {
            throw new ArgumentException("agentSettings.agentId is required.");
        }

        if (string.IsNullOrWhiteSpace(PairCode))
        {
            throw new ArgumentException("agentSettings.pairCode is required.");
        }

        if (string.IsNullOrWhiteSpace(MockControlServerUrl))
        {
            throw new ArgumentException("agentSettings.mockControlServerUrl is required.");
        }

        if (HeartbeatIntervalSeconds <= 0)
        {
            throw new ArgumentException("agentSettings.heartbeatIntervalSeconds must be greater than 0.");
        }
    }
}
