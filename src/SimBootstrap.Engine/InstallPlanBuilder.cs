using System;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine;

public class InstallPlanBuilder : IInstallPlanBuilder
{
    public InstallerExecutionPlan BuildPlan(PackageManifest manifest, string sourcePath)
    {
        if (!Enum.TryParse<InstallerType>(manifest.InstallerType, true, out var type))
        {
            throw new NotSupportedException($"Unsupported installer type: '{manifest.InstallerType}'");
        }

        string command = string.Empty;
        string arguments = string.Empty;
        string workingDirectory = System.IO.Path.GetDirectoryName(sourcePath) ?? string.Empty;

        switch (type)
        {
            case InstallerType.Msi:
                command = "msiexec.exe";
                arguments = $"/i \"{sourcePath}\" {manifest.InstallArguments}".Trim();
                break;
            case InstallerType.Exe:
                command = sourcePath;
                arguments = manifest.InstallArguments ?? string.Empty;
                break;
            case InstallerType.PowerShell:
                command = "powershell.exe";
                arguments = $"-ExecutionPolicy Bypass -File \"{sourcePath}\" {manifest.InstallArguments}".Trim();
                break;
            case InstallerType.Winget:
                command = "winget";
                arguments = $"install {manifest.Id} {manifest.InstallArguments}".Trim();
                break;
            case InstallerType.Zip:
            case InstallerType.Portable:
            case InstallerType.Custom:
                command = sourcePath;
                arguments = manifest.InstallArguments ?? string.Empty;
                break;
        }

        return new InstallerExecutionPlan(manifest.Id, type, command, arguments, workingDirectory);
    }
}
