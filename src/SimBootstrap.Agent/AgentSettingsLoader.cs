using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SimBootstrap.Agent;

public static class AgentSettingsLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<AgentSettings> LoadAsync(string configPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException($"Agent configuration file not found: {configPath}", configPath);
        }

        await using var stream = File.OpenRead(configPath);
        var settings = await JsonSerializer.DeserializeAsync<AgentSettings>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Agent configuration file is empty or invalid.");

        settings.Validate();
        return settings;
    }
}
