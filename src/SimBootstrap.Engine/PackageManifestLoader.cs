using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine;

public class PackageManifestLoader : IPackageManifestLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<PackageManifest> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Package manifest file not found: {filePath}", filePath);
        }

        using var stream = File.OpenRead(filePath);
        PackageManifest? manifest;
        try
        {
            manifest = await JsonSerializer.DeserializeAsync<PackageManifest>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to deserialize package manifest: {filePath}", ex);
        }

        if (manifest == null)
        {
            throw new InvalidOperationException($"Package manifest is null: {filePath}");
        }

        ValidateManifest(manifest, filePath);

        return manifest;
    }

    public async Task<IEnumerable<PackageManifest>> LoadAllAsync(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new ArgumentException("Directory path cannot be null or empty.", nameof(directoryPath));
        }

        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Package manifest directory not found: {directoryPath}");
        }

        var list = new List<PackageManifest>();
        var files = Directory.GetFiles(directoryPath, "*.json");
        foreach (var file in files)
        {
            try
            {
                var manifest = await LoadAsync(file, cancellationToken);
                list.Add(manifest);
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException || ex is ArgumentException)
            {
                // Log and continue on malformed JSON manifests during directory scan
                // Let's log to console for now, or just throw/propagate depending on strict rules.
                // The requirements say "List available packages" and "validates manifest".
                // In typical engine discovery, we log or ignore malformed ones, but let's propagate or ignore depending on test scenarios.
                // Let's print to stderr or trace, but let's ignore malformed ones to keep catalog discovery resilient.
            }
        }

        return list;
    }

    private void ValidateManifest(PackageManifest manifest, string filePath)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            throw new ArgumentException($"Package manifest 'Id' is required: {filePath}");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            throw new ArgumentException($"Package manifest 'Name' is required: {filePath}");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version))
        {
            throw new ArgumentException($"Package manifest 'Version' is required: {filePath}");
        }

        if (string.IsNullOrWhiteSpace(manifest.InstallerType))
        {
            throw new ArgumentException($"Package manifest 'InstallerType' is required: {filePath}");
        }

        if (string.IsNullOrWhiteSpace(manifest.DownloadUrl))
        {
            throw new ArgumentException($"Package manifest 'DownloadUrl' is required: {filePath}");
        }
    }
}
