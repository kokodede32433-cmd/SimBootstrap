using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SimBootstrap.Agent;
using SimBootstrap.Engine.Provisioning;

namespace SimBootstrap.CLI;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("provisioning", StringComparison.OrdinalIgnoreCase))
        {
            await RunProvisioningCliAsync(args);
            return;
        }

        if (args.Length > 0 && args[0].Equals("agent", StringComparison.OrdinalIgnoreCase))
        {
            await RunAgentCliAsync(args);
            return;
        }

        Console.WriteLine("SimBootstrap CLI");
        Console.WriteLine("Usage:");
        Console.WriteLine("  SimBootstrap.CLI provisioning [options]");
        Console.WriteLine("  SimBootstrap.CLI agent run-once [--config <path>]");
        Console.WriteLine("  SimBootstrap.CLI agent validate-config [--config <path>]");
    }

    private static async Task RunProvisioningCliAsync(string[] args)
    {
        bool dryRun = true;
        string configPath = "config/provisioning.json";
        bool applySpecified = false;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--dry-run", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = true;
            }
            else if (args[i].Equals("--apply", StringComparison.OrdinalIgnoreCase))
            {
                dryRun = false;
                applySpecified = true;
            }
            else if (args[i].Equals("--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                configPath = args[i + 1];
                i++;
            }
        }

        if (!applySpecified && dryRun)
        {
            Console.WriteLine("[INFO] Running in default Dry-Run Mode. No actual system changes will be applied.");
        }

        if (!File.Exists(configPath))
        {
            Console.WriteLine($"[ERROR] Configuration file not found at: '{configPath}'");
            Environment.Exit(1);
        }

        ProvisioningConfig config;
        try
        {
            var json = await File.ReadAllTextAsync(configPath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            config = JsonSerializer.Deserialize<ProvisioningConfig>(json, options) 
                     ?? throw new InvalidOperationException("Failed to deserialize configuration.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Failed to load configuration: {ex.Message}");
            Environment.Exit(1);
            return;
        }

        var runner = new PowerShellRunner();
        var checker = new WindowsCapabilityChecker(runner);
        var engine = new ProvisioningEngine(runner, checker);

        try
        {
            var result = await engine.RunProvisioningAsync(config, dryRun);

            // Print logs
            Console.WriteLine("\n=== PROVISIONING LOGS ===");
            foreach (var log in result.EngineLogs)
            {
                Console.WriteLine($"[{log.Timestamp:yyyy-MM-dd HH:mm:ss}] [{log.Level}] {log.Message}");
            }

            Console.WriteLine("\n=== STEP RESULTS ===");
            foreach (var stepResult in result.StepResults)
            {
                Console.WriteLine($"Step '{stepResult.StepName}': {stepResult.Status}");
                foreach (var log in stepResult.Logs)
                {
                    Console.WriteLine($"  {log}");
                }
                if (stepResult.ErrorMessage != null)
                {
                    Console.WriteLine($"  Error: {stepResult.ErrorMessage}");
                }
            }

            Console.WriteLine("\n=== SUMMARY ===");
            if (result.Success)
            {
                Console.WriteLine("SUCCESS: Provisioning completed successfully.");
                Environment.Exit(0);
            }
            else
            {
                Console.WriteLine("FAILED: Provisioning failed or was aborted.");
                Environment.Exit(1);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Unhandled exception during provisioning: {ex.Message}");
            Environment.Exit(1);
        }
    }

    private static async Task RunAgentCliAsync(string[] args)
    {
        var command = args.Length > 1 ? args[1] : string.Empty;
        var configPath = "config/agentsettings.json";

        for (var i = 2; i < args.Length; i++)
        {
            if (args[i].Equals("--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                configPath = args[i + 1];
                i++;
            }
        }

        try
        {
            if (command.Equals("validate-config", StringComparison.OrdinalIgnoreCase))
            {
                await AgentSettingsLoader.LoadAsync(configPath);
                Console.WriteLine($"SUCCESS: Agent configuration is valid: {configPath}");
                Environment.Exit(0);
                return;
            }

            if (command.Equals("run-once", StringComparison.OrdinalIgnoreCase))
            {
                var settings = await AgentSettingsLoader.LoadAsync(configPath);
                var runner = new AgentRunner(new MockControlServerClient(), new AgentLogWriter());
                var result = await runner.RunOnceAsync(settings);

                Console.WriteLine("\n=== AGENT RUN-ONCE EVENTS ===");
                foreach (var entry in result.Events)
                {
                    Console.WriteLine(entry);
                }

                Console.WriteLine("\n=== SUMMARY ===");
                Console.WriteLine(result.Success ? "SUCCESS: Agent run-once completed." : "FAILED: Agent run-once failed.");
                Environment.Exit(result.Success ? 0 : 1);
                return;
            }

            Console.WriteLine("[ERROR] Unknown agent command.");
            Console.WriteLine("Usage: SimBootstrap.CLI agent [run-once|validate-config] [--config <path>]");
            Environment.Exit(1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Agent command failed: {ex.Message}");
            Environment.Exit(1);
        }
    }
}
