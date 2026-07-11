using System;

namespace SimBootstrap.Agent;

public class AgentSettings
{
    public string AgentId { get; set; } = string.Empty;
    public string LocationId { get; set; } = string.Empty;
    public string ClubId { get; set; } = string.Empty;
    public string MachineCredential { get; set; } = string.Empty;
    public string ControlServerUrl { get; set; } = string.Empty;
    public int HeartbeatIntervalSeconds { get; set; } = 60;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AgentId))
        {
            throw new ArgumentException("agentSettings.agentId is required.");
        }

        if (string.IsNullOrWhiteSpace(LocationId))
        {
            throw new ArgumentException("agentSettings.locationId is required.");
        }

        if (string.IsNullOrWhiteSpace(ClubId))
        {
            throw new ArgumentException("agentSettings.clubId is required.");
        }

        if (string.IsNullOrWhiteSpace(MachineCredential))
        {
            throw new ArgumentException("agentSettings.machineCredential is required.");
        }

        if (string.IsNullOrWhiteSpace(ControlServerUrl))
        {
            throw new ArgumentException("agentSettings.controlServerUrl is required.");
        }

        if (HeartbeatIntervalSeconds <= 0)
        {
            throw new ArgumentException("agentSettings.heartbeatIntervalSeconds must be greater than 0.");
        }
    }
}
