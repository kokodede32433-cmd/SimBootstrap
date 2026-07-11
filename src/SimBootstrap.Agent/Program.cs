using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace SimBootstrap.Agent;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var options = AgentCommandLine.Parse(args);

        try
        {
            if (options.Command.Equals("service", StringComparison.OrdinalIgnoreCase))
            {
                await RunServiceHostAsync(options.ConfigPath, args);
                return;
            }

            if (options.Command.Equals("validate-config", StringComparison.OrdinalIgnoreCase))
            {
                await AgentSettingsLoader.LoadAsync(options.ConfigPath);
                Console.WriteLine($"Agent configuration is valid: {options.ConfigPath}");
                return;
            }

            if (TryMapServiceCommand(options.Command, out var serviceCommand))
            {
                var manager = new WindowsServiceManager(new ProcessRunner());
                var result = await manager.ExecuteAsync(serviceCommand, options.ExecutablePath, options.ConfigPath);
                foreach (var command in result.Commands)
                {
                    Console.WriteLine(command);
                }
                Console.WriteLine(result.Message);
                Environment.Exit(result.Success ? 0 : 1);
                return;
            }

            if (options.Command.Equals("run-once", StringComparison.OrdinalIgnoreCase))
            {
                var settings = await AgentSettingsLoader.LoadAsync(options.ConfigPath);
                var runner = new AgentRunner(new MockControlServerClient(), new AgentLogWriter());
                var result = await runner.RunOnceAsync(settings);

                foreach (var entry in result.Events)
                {
                    Console.WriteLine(entry);
                }

                Environment.Exit(result.Success ? 0 : 1);
                return;
            }

            Console.WriteLine("Usage: SimBootstrap.Agent [run-once|validate-config|install-service|uninstall-service|start-service|stop-service|service-status] [--config <path>] [--exe <path>]");
            Environment.Exit(1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] {ex.Message}");
            Environment.Exit(1);
        }
    }

    public static bool TryMapServiceCommand(string command, out AgentServiceCommandKind kind)
    {
        if (command.Equals("install-service", StringComparison.OrdinalIgnoreCase))
        {
            kind = AgentServiceCommandKind.Install;
            return true;
        }

        if (command.Equals("uninstall-service", StringComparison.OrdinalIgnoreCase))
        {
            kind = AgentServiceCommandKind.Uninstall;
            return true;
        }

        if (command.Equals("start-service", StringComparison.OrdinalIgnoreCase))
        {
            kind = AgentServiceCommandKind.Start;
            return true;
        }

        if (command.Equals("stop-service", StringComparison.OrdinalIgnoreCase))
        {
            kind = AgentServiceCommandKind.Stop;
            return true;
        }

        if (command.Equals("service-status", StringComparison.OrdinalIgnoreCase))
        {
            kind = AgentServiceCommandKind.Status;
            return true;
        }

        kind = default;
        return false;
    }

    private static async Task RunServiceHostAsync(string configPath, string[] args)
    {
        var settings = await AgentSettingsLoader.LoadAsync(configPath);
        var builder = Host.CreateDefaultBuilder(args)
            .UseWindowsService(options =>
            {
                options.ServiceName = AgentPaths.ServiceName;
            })
            .ConfigureServices(services =>
            {
                services.AddSingleton(new AgentServiceOptions { Settings = settings });
                services.AddSingleton<IAgentLogWriter, AgentLogWriter>();
                services.AddSingleton<IMockControlServerClient, MockControlServerClient>();
                services.AddSingleton<AgentRunner>();
                services.AddHostedService<AgentWorker>();
            });

        await builder.Build().RunAsync();
    }
}
