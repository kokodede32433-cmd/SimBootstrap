using System;
using System.IO;

namespace SimBootstrap.Agent;

public sealed record AgentCommandLineOptions(
    string Command,
    string ConfigPath,
    string ExecutablePath);

public static class AgentCommandLine
{
    public static AgentCommandLineOptions Parse(string[] args, string? defaultExecutablePath = null)
    {
        var command = args.Length > 0 ? args[0] : "run-once";
        string? explicitConfigPath = null;
        var executablePath = defaultExecutablePath ?? Environment.ProcessPath ?? "SimBootstrap.Agent.exe";

        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                explicitConfigPath = args[i + 1];
                i++;
            }
            else if (args[i].Equals("--exe", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                executablePath = args[i + 1];
                i++;
            }
        }

        return new AgentCommandLineOptions(
            command,
            AgentPaths.GetConfigPath(explicitConfigPath),
            Path.GetFullPath(executablePath));
    }
}
