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

        var pairingCode = GetPairingCode(args);
        if (string.IsNullOrWhiteSpace(pairingCode))
        {
            Console.Write("Pair Code: ");
            pairingCode = Console.ReadLine() ?? string.Empty;
        }

        var processRunner = new ProcessRunner();
        var orchestrator = new SetupOrchestrator(
            new SetupPlatform(),
            new RealPairingClient(),
            new SetupFileSystem(),
            new EmbeddedAgentPayloadExtractor(),
            new ProvisioningSetupProvisioner(),
            new AgentServiceSetupManager(processRunner),
            new WindowsAgentConfigAcl(processRunner));

        var result = await orchestrator.RunAsync(new SetupOptions(pairingCode, NonInteractive: false));
        Console.WriteLine(result.Message);
        Environment.Exit(result.Success ? 0 : 1);
    }

    private static string? GetPairingCode(string[] args)
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
