namespace SimBootstrap.Engine.Agent;

public class AgentSettings
{
    public string ControlServerUrl { get; set; } = string.Empty;
    public string PairCode { get; set; } = string.Empty;
    public int PollingIntervalSeconds { get; set; } = 5;
    public string MachineName { get; set; } = string.Empty;
    public bool DryRun { get; set; }
}
