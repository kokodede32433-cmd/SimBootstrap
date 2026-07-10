using System.Collections.Generic;

namespace SimBootstrap.Contracts;

public record PackageManifest(
    string Id,
    string Name,
    string Version,
    string InstallerType,
    string DownloadUrl,
    string InstallArguments,
    List<VerifyRule> VerifyRules,
    List<string> Dependencies
);

public record VerifyRule(
    string Type,
    string Path,
    string Value
);
