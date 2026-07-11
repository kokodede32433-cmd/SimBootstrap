using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SimBootstrap.Agent;

public static class AgentPaths
{
    public const string ServiceName = "SimBootstrapAgent";
    public const string DisplayName = "SimBootstrap Agent";

    public static string GetConfigPath(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SimBootstrap",
                "config",
                "agentsettings.json");
        }

        return Path.Combine(AppContext.BaseDirectory, "config", "agentsettings.json");
    }

    public static string GetLogRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SimBootstrap",
                "logs");
        }

        return Path.Combine(AppContext.BaseDirectory, "logs");
    }

    public static string GetProgramDataConfigRoot()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SimBootstrap",
                "config");
        }

        return Path.Combine(AppContext.BaseDirectory, "config");
    }
}
