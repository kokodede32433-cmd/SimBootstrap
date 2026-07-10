using System;
using System.Threading;
using System.Threading.Tasks;

namespace SimBootstrap.Engine.Provisioning;

public class CommandResult
{
    public int ExitCode { get; }
    public string StdOut { get; }
    public string StdErr { get; }
    public bool Succeeded => ExitCode == 0;

    public CommandResult(int exitCode, string stdOut, string stdErr)
    {
        ExitCode = exitCode;
        StdOut = stdOut;
        StdErr = stdErr;
    }
}

public interface ICommandRunner
{
    Task<CommandResult> RunPowerShellAsync(string command, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}
