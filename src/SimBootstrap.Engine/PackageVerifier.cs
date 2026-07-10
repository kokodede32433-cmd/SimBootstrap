using System;
using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine;

public class PackageVerifier : IPackageVerifier
{
    public Task<bool> VerifyAsync(PackageManifest manifest, CancellationToken cancellationToken = default)
    {
        if (manifest.VerifyRules == null || manifest.VerifyRules.Count == 0)
        {
            return Task.FromResult(true);
        }

        foreach (var rule in manifest.VerifyRules)
        {
            if ("fail".Equals(rule.Type, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(false);
            }
        }

        return Task.FromResult(true);
    }
}
