using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine.Agent;

public class MockControlServer
{
    private readonly HashSet<string> _validPairCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "VALID_PAIR_123",
        "SIMBOOTSTRAP_PROD",
        "TEST_CODE"
    };

    public class AgentRegistrationState
    {
        public AgentId AgentId { get; set; }
        public MachineId MachineId { get; set; }
        public string MachineName { get; set; }
        public AgentStatus Status { get; set; }
        public DateTime LastHeartbeatUtc { get; set; }

        public AgentRegistrationState(AgentId agentId, MachineId machineId, string machineName)
        {
            AgentId = agentId;
            MachineId = machineId;
            MachineName = machineName;
            Status = AgentStatus.Registered;
            LastHeartbeatUtc = DateTime.UtcNow;
        }
    }

    private readonly ConcurrentDictionary<string, AgentRegistrationState> _registeredAgents = new();
    private readonly ConcurrentDictionary<string, List<BootstrapTask>> _pendingTasks = new();
    private readonly ConcurrentDictionary<string, List<BootstrapTaskResult>> _submittedResults = new();
    private readonly ConcurrentDictionary<string, List<string>> _ackedTasks = new();

    public void AddValidPairCode(string code) => _validPairCodes.Add(code);

    public AgentRegistrationResult Register(AgentRegistrationRequest request)
    {
        if (!_validPairCodes.Contains(request.PairCode.Value))
        {
            return new AgentRegistrationResult(false, null, "Invalid pairing code.");
        }

        var agentIdValue = "agent-" + Guid.NewGuid().ToString("n")[..8];
        var agentId = new AgentId(agentIdValue);
        var state = new AgentRegistrationState(agentId, request.MachineId, request.MachineName);

        _registeredAgents[agentIdValue] = state;
        _pendingTasks[agentIdValue] = new List<BootstrapTask>();
        _submittedResults[agentIdValue] = new List<BootstrapTaskResult>();
        _ackedTasks[agentIdValue] = new List<string>();

        return new AgentRegistrationResult(true, agentId, null);
    }

    public void QueueTask(AgentId agentId, BootstrapTask task)
    {
        if (_pendingTasks.TryGetValue(agentId.Value, out var tasks))
        {
            tasks.Add(task);
        }
    }

    public List<BootstrapTask> Poll(PollRequest request)
    {
        if (!_registeredAgents.TryGetValue(request.AgentId.Value, out var state))
        {
            throw new InvalidOperationException("Agent not registered.");
        }

        state.LastHeartbeatUtc = DateTime.UtcNow;
        state.Status = request.CurrentStatus;

        if (_pendingTasks.TryGetValue(request.AgentId.Value, out var tasks))
        {
            var pending = tasks.ToList();
            tasks.Clear(); // Clear from queue once polled
            return pending;
        }

        return new List<BootstrapTask>();
    }

    public bool AckTask(AgentId agentId, string taskId)
    {
        if (!_registeredAgents.ContainsKey(agentId.Value)) return false;
        
        if (_ackedTasks.TryGetValue(agentId.Value, out var acks))
        {
            acks.Add(taskId);
        }
        return true;
    }

    public bool SubmitResult(AgentId agentId, string taskId, BootstrapTaskResult result)
    {
        if (!_registeredAgents.ContainsKey(agentId.Value)) return false;

        if (_submittedResults.TryGetValue(agentId.Value, out var results))
        {
            results.Add(result);
        }
        return true;
    }

    public bool Heartbeat(AgentHeartbeat heartbeat)
    {
        if (_registeredAgents.TryGetValue(heartbeat.AgentId.Value, out var state))
        {
            state.LastHeartbeatUtc = DateTime.UtcNow;
            state.Status = heartbeat.CurrentStatus;
            return true;
        }
        return false;
    }

    public AgentRegistrationState? GetAgentState(AgentId agentId)
    {
        _registeredAgents.TryGetValue(agentId.Value, out var state);
        return state;
    }

    public List<BootstrapTaskResult> GetResults(AgentId agentId)
    {
        _submittedResults.TryGetValue(agentId.Value, out var results);
        return results ?? new List<BootstrapTaskResult>();
    }

    public List<string> GetAckedTaskIds(AgentId agentId)
    {
        _ackedTasks.TryGetValue(agentId.Value, out var acks);
        return acks ?? new List<string>();
    }
}
