using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SimBootstrap.Engine;

public class InstallationSession : IInstallationSession
{
    public string SessionId { get; }
    public string PackageId { get; }
    public string Status { get; set; }
    public DateTime StartedAtUtc { get; }
    public DateTime? CompletedAtUtc { get; set; }
    public List<string> Logs { get; } = new();

    public InstallationSession(string packageId)
    {
        SessionId = Guid.NewGuid().ToString();
        PackageId = packageId;
        Status = "Pending";
        StartedAtUtc = DateTime.UtcNow;
    }

    public Task CompleteSessionAsync(CancellationToken cancellationToken = default)
    {
        Status = "Completed";
        CompletedAtUtc = DateTime.UtcNow;
        return Task.CompletedTask;
    }
}
