using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using SimBootstrap.Engine.Provisioning;

namespace SimBootstrap.UI;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("provisioning", StringComparison.OrdinalIgnoreCase))
        {
            await RunProvisioningCliAsync(args);
            return;
        }

        // GUI Mode
#if WINDOWS
        RunWpfApp();
#else
        Console.WriteLine("WPF UI is not supported on this platform. Please run SimBootstrap in provisioning CLI mode:");
        Console.WriteLine("  SimBootstrap provisioning --dry-run");
        Console.WriteLine("  SimBootstrap provisioning --apply --config <path>");
        Environment.Exit(1);
#endif
    }

#if WINDOWS
    [STAThread]
    private static void RunWpfApp()
    {
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
#endif

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
}
