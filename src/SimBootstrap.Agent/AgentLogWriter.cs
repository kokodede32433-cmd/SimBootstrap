using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SimBootstrap.Agent;

public interface IAgentLogWriter
{
    Task WriteAsync(string message, CancellationToken cancellationToken = default);
}

public sealed class AgentLogWriter : IAgentLogWriter
{
    private readonly string _logFilePath;

    public AgentLogWriter(string? logRoot = null)
    {
        var root = logRoot ?? AgentPaths.GetLogRoot();
        Directory.CreateDirectory(root);
        _logFilePath = Path.Combine(root, "simbootstrap-agent.log");
    }

    public async Task WriteAsync(string message, CancellationToken cancellationToken = default)
    {
        var line = $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}";
        await File.AppendAllTextAsync(_logFilePath, line, cancellationToken);
    }

}

public sealed class InMemoryAgentLogWriter : IAgentLogWriter
{
    public List<string> Messages { get; } = new();

    public Task WriteAsync(string message, CancellationToken cancellationToken = default)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }
}
