using System;
using System.Collections.Generic;

namespace SimBootstrap.Contracts;

public enum InstallStatus
{
    Pending,
    Running,
    Completed,
    Failed
}

public enum InstallerType
{
    Msi,
    Exe,
    Zip,
    Portable,
    PowerShell,
    Winget,
    Custom
}

public record InstallRequest(
    string PackageId,
    InstallerType InstallerType,
    string SourcePath,
    string? InstallArguments
);

public record InstallerInstallResult(
    bool IsSuccess,
    int ExitCode,
    string? ErrorMessage,
    List<string> Logs
);

public record InstallerExecutionPlan(
    string PackageId,
    InstallerType InstallerType,
    string Command,
    string Arguments,
    string WorkingDirectory
);

public record InstallerArgumentSet(
    string RawArguments
);
