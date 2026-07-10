using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine;

public class InstallerProviderRegistry : IInstallerProviderRegistry
{
    private readonly ConcurrentDictionary<InstallerType, IInstallerProvider> _providers = new();

    public void Register(IInstallerProvider provider)
    {
        _providers[provider.SupportedType] = provider;
    }

    public IInstallerProvider GetProvider(InstallerType type)
    {
        if (!_providers.TryGetValue(type, out var provider))
        {
            throw new KeyNotFoundException($"No installer provider registered for type '{type}'");
        }
        return provider;
    }
}
