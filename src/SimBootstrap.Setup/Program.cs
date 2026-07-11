using System;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Agent;

namespace SimBootstrap.Setup;

public static class Program
{
    private const string MutexName = "Global\\SimBootstrap.Setup";

    public static async Task Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            Console.WriteLine("SimBootstrap Setup is already running.");
            Environment.Exit(1);
            return;
        }

        var pairCode = GetPairCode(args);
        if (string.IsNullOrWhiteSpace(pairCode))
        {
            Console.Write("Pair Code: ");
            pairCode = Console.ReadLine() ?? string.Empty;
        }

        var orchestrator = new SetupOrchestrator(
            new SetupPlatform(),
            new PreviewPairingClient(),
            new SetupFileSystem(),
            new EmbeddedAgentPayloadExtractor(),
            new ProvisioningSetupProvisioner(),
            new AgentServiceSetupManager(new ProcessRunner()));

        var result = await orchestrator.RunAsync(new SetupOptions(pairCode, NonInteractive: false));
        Console.WriteLine(result.Message);
        Environment.Exit(result.Success ? 0 : 1);
    }

    private static string? GetPairCode(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--pair-code", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
