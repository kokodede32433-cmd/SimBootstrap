using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SimBootstrap.Agent;

public sealed class AgentWorker : BackgroundService
{
    private readonly AgentRunner _runner;
    private readonly AgentSettings _settings;
    private readonly IAgentLogWriter _logWriter;

    public AgentWorker(AgentRunner runner, IOptions<AgentServiceOptions> options, IAgentLogWriter logWriter)
    {
        _runner = runner;
        _settings = options.Value.Settings;
        _logWriter = logWriter;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _logWriter.WriteAsync("SimBootstrap Agent service loop started.", stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _runner.RunOnceAsync(_settings, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                await _logWriter.WriteAsync($"Agent service loop error: {ex.Message}", stoppingToken);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_settings.HeartbeatIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        await _logWriter.WriteAsync("SimBootstrap Agent service loop stopped.", CancellationToken.None);
    }
}
