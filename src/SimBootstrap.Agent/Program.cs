using System;
using System.Threading.Tasks;

namespace SimBootstrap.Agent;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var command = args.Length > 0 ? args[0] : "run-once";
        var configPath = GetConfigPath(args);

        try
        {
            if (command.Equals("validate-config", StringComparison.OrdinalIgnoreCase))
            {
                await AgentSettingsLoader.LoadAsync(configPath);
                Console.WriteLine($"Agent configuration is valid: {configPath}");
                return;
            }

            if (!command.Equals("run-once", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Usage: SimBootstrap.Agent [run-once|validate-config] [--config <path>]");
                Environment.Exit(1);
                return;
            }

            var settings = await AgentSettingsLoader.LoadAsync(configPath);
            var runner = new AgentRunner(new MockControlServerClient(), new AgentLogWriter());
            var result = await runner.RunOnceAsync(settings);

            foreach (var entry in result.Events)
            {
                Console.WriteLine(entry);
            }

            Environment.Exit(result.Success ? 0 : 1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] {ex.Message}");
            Environment.Exit(1);
        }
    }

    private static string GetConfigPath(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return "config/agentsettings.json";
    }
}
