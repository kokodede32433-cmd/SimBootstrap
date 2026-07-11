using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SimBootstrap.Agent;

public enum AgentServiceCommandKind
{
    Install,
    Uninstall,
    Start,
    Stop,
    Status
}

public sealed record ServiceCommandResult(bool Success, string Message, IReadOnlyList<string> Commands);

public sealed class WindowsServiceInstallPlan
{
    public string ServiceName { get; init; } = AgentPaths.ServiceName;
    public string DisplayName { get; init; } = AgentPaths.DisplayName;
    public string Description { get; init; } = "Runs the local SimBootstrap Agent.";
    public string ExecutablePath { get; init; } = string.Empty;
    public string ConfigPath { get; init; } = string.Empty;
    public string BinPath { get; init; } = string.Empty;
    public List<string> Commands { get; } = new();
}

public static class WindowsServiceCommandBuilder
{
    public static WindowsServiceInstallPlan BuildInstallPlan(
        string executablePath,
        string configPath,
        string serviceName = AgentPaths.ServiceName,
        string displayName = AgentPaths.DisplayName,
        string description = "Runs the local SimBootstrap Agent.",
        string? binPath = null)
    {
        var resolvedBinPath = binPath ?? $"\"{executablePath}\" service --config \"{configPath}\"";
        var plan = new WindowsServiceInstallPlan
        {
            ServiceName = serviceName,
            DisplayName = displayName,
            Description = description,
            ExecutablePath = executablePath,
            ConfigPath = configPath,
            BinPath = resolvedBinPath
        };

        plan.Commands.Add($"sc.exe query {serviceName}");
        plan.Commands.Add($"sc.exe create {serviceName} binPath= {Quote(resolvedBinPath)} DisplayName= {Quote(displayName)} start= delayed-auto");
        plan.Commands.Add($"sc.exe description {serviceName} {Quote(description)}");
        plan.Commands.Add($"sc.exe failure {serviceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000");
        plan.Commands.Add($"sc.exe failureflag {serviceName} 1");
        return plan;
    }

    public static string BuildUninstallQueryCommand(string serviceName = AgentPaths.ServiceName) => $"sc.exe query {serviceName}";
    public static string BuildUninstallDeleteCommand(string serviceName = AgentPaths.ServiceName) => $"sc.exe delete {serviceName}";
    public static string BuildStartCommand(string serviceName = AgentPaths.ServiceName) => $"sc.exe start {serviceName}";
    public static string BuildStopCommand(string serviceName = AgentPaths.ServiceName) => $"sc.exe stop {serviceName}";
    public static string BuildStatusCommand(string serviceName = AgentPaths.ServiceName) => $"sc.exe query {serviceName}";

    private static string Quote(string value) => $"\"{value}\"";
}

public interface IProcessRunner
{
    Task<CommandExecutionResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default);
}

public sealed record CommandExecutionResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<CommandExecutionResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start process: {fileName}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new CommandExecutionResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
}

public sealed class WindowsServiceManager
{
    private readonly IProcessRunner _processRunner;
    private readonly Func<bool> _isWindows;
    private readonly string _serviceName;
    private readonly string _displayName;
    private readonly string _description;
    private readonly Func<string, string, string> _binPathFactory;

    public WindowsServiceManager(
        IProcessRunner processRunner,
        Func<bool>? isWindows = null,
        string serviceName = AgentPaths.ServiceName,
        string displayName = AgentPaths.DisplayName,
        string description = "Runs the local SimBootstrap Agent.",
        Func<string, string, string>? binPathFactory = null)
    {
        _processRunner = processRunner;
        _isWindows = isWindows ?? (() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
        _serviceName = serviceName;
        _displayName = displayName;
        _description = description;
        _binPathFactory = binPathFactory ?? ((executablePath, configPath) => $"\"{executablePath}\" service --config \"{configPath}\"");
    }

    public async Task<ServiceCommandResult> ExecuteAsync(
        AgentServiceCommandKind command,
        string executablePath,
        string configPath,
        CancellationToken cancellationToken = default)
    {
        if (!_isWindows())
        {
            return new ServiceCommandResult(false, "Windows service commands can only run on Windows.", Array.Empty<string>());
        }

        return command switch
        {
            AgentServiceCommandKind.Install => await InstallAsync(executablePath, configPath, cancellationToken),
            AgentServiceCommandKind.Uninstall => await UninstallAsync(cancellationToken),
            AgentServiceCommandKind.Start => await RunServiceCommandAsync(WindowsServiceCommandBuilder.BuildStartCommand(_serviceName), "Service start requested.", cancellationToken),
            AgentServiceCommandKind.Stop => await RunServiceCommandAsync(WindowsServiceCommandBuilder.BuildStopCommand(_serviceName), "Service stop requested.", cancellationToken),
            AgentServiceCommandKind.Status => await RunServiceCommandAsync(WindowsServiceCommandBuilder.BuildStatusCommand(_serviceName), "Service status queried.", cancellationToken),
            _ => new ServiceCommandResult(false, $"Unsupported service command: {command}", Array.Empty<string>())
        };
    }

    private async Task<ServiceCommandResult> InstallAsync(string executablePath, string configPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AgentPaths.GetProgramDataConfigRoot());
        Directory.CreateDirectory(AgentPaths.GetLogRoot());
        CopyDefaultConfigIfMissing(configPath);

        var binPath = _binPathFactory(executablePath, configPath);
        var plan = WindowsServiceCommandBuilder.BuildInstallPlan(executablePath, configPath, _serviceName, _displayName, _description, binPath);
        var commands = new List<string> { plan.Commands[0] };
        var query = await RunScAsync("query", _serviceName, cancellationToken);
        if (query.ExitCode == 0)
        {
            return new ServiceCommandResult(true, "Service already installed.", commands);
        }

        var createArgs = $"create {_serviceName} binPath= \"{binPath.Replace("\"", "\\\"", StringComparison.Ordinal)}\" DisplayName= \"{_displayName}\" start= delayed-auto";
        commands.Add($"sc.exe {createArgs}");
        var create = await RunScAsync(createArgs, cancellationToken);
        if (!create.Succeeded)
        {
            return new ServiceCommandResult(false, $"Install service failed: {create.StdErr}{create.StdOut}".Trim(), commands);
        }

        foreach (var args in new[]
        {
            $"description {_serviceName} \"{_description}\"",
            $"failure {_serviceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000",
            $"failureflag {_serviceName} 1"
        })
        {
            commands.Add($"sc.exe {args}");
            var result = await RunScAsync(args, cancellationToken);
            if (!result.Succeeded)
            {
                return new ServiceCommandResult(false, $"Configure service failed: {result.StdErr}{result.StdOut}".Trim(), commands);
            }
        }

        return new ServiceCommandResult(true, "Service installed.", commands);
    }

    private async Task<ServiceCommandResult> UninstallAsync(CancellationToken cancellationToken)
    {
        var commands = new List<string> { WindowsServiceCommandBuilder.BuildUninstallQueryCommand(_serviceName) };
        var query = await RunScAsync("query", _serviceName, cancellationToken);
        if (query.ExitCode != 0)
        {
            return new ServiceCommandResult(true, "Service is not installed.", commands);
        }

        var deleteArgs = $"delete {_serviceName}";
        commands.Add($"sc.exe {deleteArgs}");
        var delete = await RunScAsync(deleteArgs, cancellationToken);
        return delete.Succeeded
            ? new ServiceCommandResult(true, "Service uninstalled.", commands)
            : new ServiceCommandResult(false, $"Uninstall service failed: {delete.StdErr}{delete.StdOut}".Trim(), commands);
    }

    private async Task<ServiceCommandResult> RunServiceCommandAsync(string command, string successMessage, CancellationToken cancellationToken)
    {
        var args = command["sc.exe ".Length..];
        var result = await RunScAsync(args, cancellationToken);
        return result.Succeeded
            ? new ServiceCommandResult(true, successMessage, new[] { command })
            : new ServiceCommandResult(false, $"{successMessage} failed: {result.StdErr}{result.StdOut}".Trim(), new[] { command });
    }

    private Task<CommandExecutionResult> RunScAsync(string command, string serviceName, CancellationToken cancellationToken)
    {
        return RunScAsync($"{command} {serviceName}", cancellationToken);
    }

    private Task<CommandExecutionResult> RunScAsync(string arguments, CancellationToken cancellationToken)
    {
        return _processRunner.RunAsync("sc.exe", arguments, cancellationToken);
    }

    private static void CopyDefaultConfigIfMissing(string configPath)
    {
        if (File.Exists(configPath))
        {
            return;
        }

        var sourcePath = Path.Combine(AppContext.BaseDirectory, "config", "agentsettings.json");
        if (File.Exists(sourcePath))
        {
            File.Copy(sourcePath, configPath, overwrite: false);
        }
    }
}
