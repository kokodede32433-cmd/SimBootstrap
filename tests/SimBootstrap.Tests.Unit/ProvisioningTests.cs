using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using SimBootstrap.Contracts;
using SimBootstrap.Engine.Provisioning;
using SimBootstrap.Engine.Provisioning.Steps;

namespace SimBootstrap.Tests.Unit;

public class ProvisioningTests
{
    private class MockCommandRunner : ICommandRunner
    {
        public List<string> RunCommands { get; } = new();
        public Func<string, CommandResult>? CommandHandler { get; set; }

        public Task<CommandResult> RunPowerShellAsync(string command, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            RunCommands.Add(command);
            if (CommandHandler != null)
            {
                return Task.FromResult(CommandHandler(command));
            }
            return Task.FromResult(new CommandResult(0, "Mock Output", string.Empty));
        }
    }

    private class AuthorizedKeysMockCommandRunner : ICommandRunner
    {
        private readonly bool _failAcl;
        private readonly bool _requireRecovery;
        private readonly bool _failRecovery;

        public List<string> RunCommands { get; } = new();
        public bool SshDirectoryExists { get; private set; }
        public bool AuthorizedKeysExists { get; private set; }
        public int KeyAddCount { get; private set; }
        public int AclApplyCount { get; private set; }
        public int RecoveryCount { get; private set; }

        public AuthorizedKeysMockCommandRunner(bool failAcl = false, bool requireRecovery = false, bool failRecovery = false)
        {
            _failAcl = failAcl;
            _requireRecovery = requireRecovery;
            _failRecovery = failRecovery;
        }

        public Task<CommandResult> RunPowerShellAsync(string command, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            RunCommands.Add(command);

            if (!command.Contains("New-Item -ItemType Directory", StringComparison.OrdinalIgnoreCase) ||
                !command.Contains("New-Item -ItemType File", StringComparison.OrdinalIgnoreCase) ||
                !command.Contains("Test-PathWithRepair -Path $authKeys", StringComparison.OrdinalIgnoreCase) ||
                !command.Contains("Set-RequiredAcl -Path $sshDir", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new CommandResult(1, string.Empty, "authorized_keys script did not create required paths safely"));
            }

            if (_requireRecovery)
            {
                if (!command.Contains("takeown @takeownArgs", StringComparison.OrdinalIgnoreCase) ||
                    !command.Contains("& icacls $Path /grant $userGrant /grant \"SYSTEM:F\" /grant \"Administrators:F\"", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new CommandResult(1, string.Empty, "authorized_keys script did not include ACL recovery commands"));
                }

                RecoveryCount++;

                if (_failRecovery)
                {
                    return Task.FromResult(new CommandResult(1, string.Empty, "authorized_keys exists but permissions could not be repaired"));
                }
            }

            SshDirectoryExists = true;
            AuthorizedKeysExists = true;

            if (_failAcl)
            {
                return Task.FromResult(new CommandResult(1, "KeyAdded", "ACL configuration failed: mocked ACL failure"));
            }

            AclApplyCount++;
            if (KeyAddCount == 0)
            {
                KeyAddCount++;
                return Task.FromResult(new CommandResult(0, "KeyAdded\nAclApplied", string.Empty));
            }

            return Task.FromResult(new CommandResult(0, "KeyAlreadyPresent\nAclApplied", string.Empty));
        }
    }

    private class MockCapabilityChecker : IWindowsCapabilityChecker
    {
        public WindowsCapabilities Capabilities { get; set; } = new()
        {
            WindowsVersion = "Windows 11 Pro Mock",
            IsAdmin = true,
            PowerShellVersion = "7.4.0",
            IsWinGetAvailable = true
        };
        public int Calls { get; private set; }

        public Task<WindowsCapabilities> CheckCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Capabilities);
        }
    }

    private class MockStep : IProvisioningStep
    {
        public string Name { get; }
        public string Description => "Mock Step Description";
        public bool IsCritical { get; }
        public bool ShouldRunResult { get; set; } = true;
        public Func<ProvisioningContext, bool, ProvisioningStepResult>? ExecuteHandler { get; set; }

        public MockStep(string name, bool isCritical)
        {
            Name = name;
            IsCritical = isCritical;
        }

        public Task<bool> ShouldRunAsync(ProvisioningContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(ShouldRunResult);
        }

        public Task<ProvisioningStepResult> ExecuteAsync(ProvisioningContext context, bool dryRun, CancellationToken cancellationToken)
        {
            if (ExecuteHandler != null)
            {
                return Task.FromResult(ExecuteHandler(context, dryRun));
            }
            return Task.FromResult(ProvisioningStepResult.Success(Name));
        }
    }

    [Fact]
    public void Config_ValidKey_PassesValidation()
    {
        var config = new ProvisioningConfig
        {
            AuthorizedPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI123456789 user@host",
            InstallGit = true,
            InstallDotNet9 = true
        };

        // Should not throw
        config.Validate();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Config_EmptyKey_ThrowsArgumentException(string? key)
    {
        var config = new ProvisioningConfig
        {
            AuthorizedPublicKey = key!,
            InstallGit = true
        };

        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Theory]
    [InlineData("invalid-ssh-key-prefix")]
    [InlineData("ssh-rsa-without-space")]
    [InlineData("ed25519 AAAAC3NzaC1lZDI1NTE5")]
    public void Config_InvalidKeyFormat_ThrowsArgumentException(string key)
    {
        var config = new ProvisioningConfig
        {
            AuthorizedPublicKey = key,
            InstallGit = true
        };

        Assert.Throws<ArgumentException>(() => config.Validate());
    }

    [Fact]
    public async Task Engine_AllStepsSucceed_ReturnsSuccess()
    {
        var runner = new MockCommandRunner();
        var checker = new MockCapabilityChecker();
        var steps = new List<IProvisioningStep>
        {
            new MockStep("Step1", isCritical: true),
            new MockStep("Step2", isCritical: false)
        };

        var engine = new ProvisioningEngine(runner, checker, steps);
        var config = new ProvisioningConfig
        {
            AuthorizedPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI123456789",
            InstallGit = true,
            InstallDotNet9 = true
        };

        var result = await engine.RunProvisioningAsync(config, dryRun: true);

        Assert.True(result.Success);
        Assert.Equal(2, result.StepResults.Count);
        Assert.Equal(ProvisioningStatus.Completed, result.StepResults[0].Status);
        Assert.Equal(ProvisioningStatus.Completed, result.StepResults[1].Status);
    }

    [Fact]
    public async Task Engine_CriticalStepFails_AbortsFlow()
    {
        var runner = new MockCommandRunner();
        var checker = new MockCapabilityChecker();
        
        var step1 = new MockStep("CriticalStep", isCritical: true)
        {
            ExecuteHandler = (ctx, dryRun) => ProvisioningStepResult.Failure("CriticalStep", "Critical Failure")
        };
        var step2 = new MockStep("NextStep", isCritical: false);
        
        var steps = new List<IProvisioningStep> { step1, step2 };

        var engine = new ProvisioningEngine(runner, checker, steps);
        var config = new ProvisioningConfig
        {
            AuthorizedPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI123456789",
            InstallGit = true
        };

        var result = await engine.RunProvisioningAsync(config, dryRun: true);

        Assert.False(result.Success);
        Assert.Single(result.StepResults); // Step 2 was never executed
        Assert.Equal("CriticalStep", result.StepResults[0].StepName);
        Assert.Equal(ProvisioningStatus.Failed, result.StepResults[0].Status);
    }

    [Fact]
    public async Task Engine_NonCriticalStepFails_ContinuesFlow()
    {
        var runner = new MockCommandRunner();
        var checker = new MockCapabilityChecker();
        
        var step1 = new MockStep("NonCriticalStep", isCritical: false)
        {
            ExecuteHandler = (ctx, dryRun) => ProvisioningStepResult.Failure("NonCriticalStep", "Non-Critical Failure")
        };
        var step2 = new MockStep("NextStep", isCritical: true);
        
        var steps = new List<IProvisioningStep> { step1, step2 };

        var engine = new ProvisioningEngine(runner, checker, steps);
        var config = new ProvisioningConfig
        {
            AuthorizedPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI123456789",
            InstallGit = true
        };

        var result = await engine.RunProvisioningAsync(config, dryRun: true);

        // Overall success is true because only critical failures block success
        Assert.True(result.Success);
        Assert.Equal(2, result.StepResults.Count);
        Assert.Equal(ProvisioningStatus.Failed, result.StepResults[0].Status);
        Assert.Equal(ProvisioningStatus.Completed, result.StepResults[1].Status);
    }

    [Fact]
    public async Task Engine_DryRunNonAdmin_DoesNotExecutePrivilegedCommands()
    {
        var runner = new MockCommandRunner
        {
            CommandHandler = command =>
            {
                if (command.Contains("git --version", StringComparison.OrdinalIgnoreCase))
                {
                    return new CommandResult(0, "git version 2.45.0.windows.1", string.Empty);
                }

                if (command.Contains("dotnet --list-sdks", StringComparison.OrdinalIgnoreCase))
                {
                    return new CommandResult(0, "9.0.100 [C:\\Program Files\\dotnet\\sdk]", string.Empty);
                }

                if (command.Contains("winget --version", StringComparison.OrdinalIgnoreCase))
                {
                    return new CommandResult(0, "v1.8.0", string.Empty);
                }

                return new CommandResult(1, string.Empty, $"Unexpected command: {command}");
            }
        };
        var checker = new MockCapabilityChecker
        {
            Capabilities = new WindowsCapabilities
            {
                WindowsVersion = "Windows 11 Pro Mock",
                IsAdmin = false,
                PowerShellVersion = "7.4.0",
                IsWinGetAvailable = true
            }
        };

        var engine = new ProvisioningEngine(runner, checker, isWindowsProvider: () => true);
        var config = new ProvisioningConfig
        {
            AuthorizedPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI123456789",
            InstallGit = true,
            InstallDotNet9 = true,
            ConfigureOpenSsh = true,
            ConfigureFirewall = true,
            DisableSleep = true
        };

        var result = await engine.RunProvisioningAsync(config, dryRun: true);

        Assert.True(result.Success);
        Assert.DoesNotContain(runner.RunCommands, IsPrivilegedProvisioningCommand);
    }

    [Fact]
    public async Task Engine_ApplyNonAdmin_FailsBeforePrivilegedSteps()
    {
        var runner = new MockCommandRunner();
        var checker = new MockCapabilityChecker
        {
            Capabilities = new WindowsCapabilities
            {
                WindowsVersion = "Windows 11 Pro Mock",
                IsAdmin = false,
                PowerShellVersion = "7.4.0",
                IsWinGetAvailable = true
            }
        };
        var step = new MockStep("PrivilegedStep", isCritical: true)
        {
            ExecuteHandler = (_, _) => ProvisioningStepResult.Success("PrivilegedStep")
        };

        var engine = new ProvisioningEngine(runner, checker, new List<IProvisioningStep> { step }, isWindowsProvider: () => true);
        var config = new ProvisioningConfig
        {
            AuthorizedPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI123456789",
            InstallGit = true
        };

        var result = await engine.RunProvisioningAsync(config, dryRun: false);

        Assert.False(result.Success);
        Assert.Empty(result.StepResults);
        Assert.Empty(runner.RunCommands);
        Assert.Contains(result.EngineLogs, log => log.Message == "Administrator privileges are required for --apply.");
    }

    [Fact]
    public async Task Steps_DryRunPrivilegedSteps_ReturnSimulatedSuccessWithoutCommands()
    {
        var runner = new MockCommandRunner
        {
            CommandHandler = command => new CommandResult(1, string.Empty, $"Unexpected command: {command}")
        };
        var checker = new MockCapabilityChecker();
        var config = new ProvisioningConfig
        {
            AuthorizedPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI123456789",
            InstallGit = true,
            InstallDotNet9 = true,
            ConfigureOpenSsh = true,
            ConfigureFirewall = true,
            DisableSleep = true
        };
        var caps = await checker.CheckCapabilitiesAsync();
        var context = new ProvisioningContext(runner, config, caps);
        var steps = new IProvisioningStep[]
        {
            new EnsureOpenSshServerInstalled(),
            new EnsureSshdRunning(),
            new EnsureSshdAutostart(),
            new EnsureFirewallRuleForSsh(),
            new EnsureAuthorizedKeys(),
            new EnsureNoSleepPowerPlan()
        };

        foreach (var step in steps)
        {
            var result = await step.ExecuteAsync(context, dryRun: true, CancellationToken.None);

            Assert.Equal(ProvisioningStatus.Completed, result.Status);
            Assert.Contains(result.Logs, log => log.Contains("[Dry-run]", StringComparison.OrdinalIgnoreCase));
        }

        Assert.Empty(runner.RunCommands);
    }

    [Fact]
    public async Task OpenSshInstalledStep_CapturedInstalledCapabilityOutput_DoesNotInstall()
    {
        const string capturedCapabilityOutput = """
Name : OpenSSH.Server~~~~0.0.1.0
State : Installed
DisplayName : ????
Description : ????
""";

        var runner = new MockCommandRunner
        {
            CommandHandler = command =>
            {
                if (command.Contains("Get-WindowsCapability", StringComparison.OrdinalIgnoreCase))
                {
                    return new CommandResult(0, capturedCapabilityOutput, string.Empty);
                }

                return new CommandResult(1, string.Empty, $"Unexpected command: {command}");
            }
        };
        var checker = new MockCapabilityChecker();
        var config = new ProvisioningConfig
        {
            AuthorizedPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI123456789",
            ConfigureOpenSsh = true
        };
        var caps = await checker.CheckCapabilitiesAsync();
        var context = new ProvisioningContext(runner, config, caps);
        var step = new EnsureOpenSshServerInstalled();

        var result = await step.ExecuteAsync(context, dryRun: false, CancellationToken.None);

        Assert.Equal(ProvisioningStatus.Completed, result.Status);
        Assert.Contains(result.Logs, log => log == "OpenSSH Server is already installed.");
        Assert.Single(runner.RunCommands);
        Assert.Contains("Get-WindowsCapability", runner.RunCommands[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runner.RunCommands, command => command.Contains("Add-WindowsCapability", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AuthorizedKeysStep_FileMissing_CreatesFileAddsKeyOnceAndIsIdempotent()
    {
        var runner = new AuthorizedKeysMockCommandRunner();
        var checker = new MockCapabilityChecker();
        var config = new ProvisioningConfig
        {
            AuthorizedPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI123456789 user@host"
        };
        var caps = await checker.CheckCapabilitiesAsync();
        var context = new ProvisioningContext(runner, config, caps);
        var step = new EnsureAuthorizedKeys();

        var firstResult = await step.ExecuteAsync(context, dryRun: false, CancellationToken.None);
        var secondResult = await step.ExecuteAsync(context, dryRun: false, CancellationToken.None);

        Assert.Equal(ProvisioningStatus.Completed, firstResult.Status);
        Assert.Equal(ProvisioningStatus.Completed, secondResult.Status);
        Assert.True(runner.SshDirectoryExists);
        Assert.True(runner.AuthorizedKeysExists);
        Assert.Equal(1, runner.KeyAddCount);
        Assert.Equal(2, runner.AclApplyCount);
        Assert.Equal(2, runner.RunCommands.Count);
        Assert.Contains(firstResult.Logs, log => log == "Public key added to authorized_keys.");
        Assert.Contains(secondResult.Logs, log => log == "Public key is already present in authorized_keys.");
        Assert.DoesNotContain(runner.RunCommands[0], config.AuthorizedPublicKey, StringComparison.Ordinal);
        Assert.Contains("FromBase64String", runner.RunCommands[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthorizedKeysStep_AclFailure_ReturnsControlledError()
    {
        var runner = new AuthorizedKeysMockCommandRunner(failAcl: true);
        var checker = new MockCapabilityChecker();
        var config = new ProvisioningConfig
        {
            AuthorizedPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI123456789 user@host"
        };
        var caps = await checker.CheckCapabilitiesAsync();
        var context = new ProvisioningContext(runner, config, caps);
        var step = new EnsureAuthorizedKeys();

        var result = await step.ExecuteAsync(context, dryRun: false, CancellationToken.None);

        Assert.Equal(ProvisioningStatus.Failed, result.Status);
        Assert.Contains("authorized_keys configuration failed: ACL configuration failed: mocked ACL failure", result.ErrorMessage);
        Assert.Contains(result.Logs, log => log.Contains("ACL configuration failed: mocked ACL failure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AuthorizedKeysStep_InaccessibleAuthorizedKeys_IncludesAclRecoveryCommands()
    {
        var runner = new AuthorizedKeysMockCommandRunner(requireRecovery: true);
        var checker = new MockCapabilityChecker();
        var config = new ProvisioningConfig
        {
            AuthorizedPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI123456789 user@host"
        };
        var caps = await checker.CheckCapabilitiesAsync();
        var context = new ProvisioningContext(runner, config, caps);
        var step = new EnsureAuthorizedKeys();

        var result = await step.ExecuteAsync(context, dryRun: false, CancellationToken.None);

        Assert.Equal(ProvisioningStatus.Completed, result.Status);
        Assert.Equal(1, runner.RecoveryCount);
        Assert.Contains("takeown @takeownArgs", runner.RunCommands[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("& icacls $Path /grant $userGrant /grant \"SYSTEM:F\" /grant \"Administrators:F\"", runner.RunCommands[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthorizedKeysStep_RecoveryFailure_ReturnsControlledError()
    {
        var runner = new AuthorizedKeysMockCommandRunner(requireRecovery: true, failRecovery: true);
        var checker = new MockCapabilityChecker();
        var config = new ProvisioningConfig
        {
            AuthorizedPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI123456789 user@host"
        };
        var caps = await checker.CheckCapabilitiesAsync();
        var context = new ProvisioningContext(runner, config, caps);
        var step = new EnsureAuthorizedKeys();

        var result = await step.ExecuteAsync(context, dryRun: false, CancellationToken.None);

        Assert.Equal(ProvisioningStatus.Failed, result.Status);
        Assert.Contains("authorized_keys exists but permissions could not be repaired", result.ErrorMessage);
    }

    [Fact]
    public async Task AuthorizedKeysStep_SuccessfulRecovery_ContinuesToKeyInjection()
    {
        var runner = new AuthorizedKeysMockCommandRunner(requireRecovery: true);
        var checker = new MockCapabilityChecker();
        var config = new ProvisioningConfig
        {
            AuthorizedPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI123456789 user@host"
        };
        var caps = await checker.CheckCapabilitiesAsync();
        var context = new ProvisioningContext(runner, config, caps);
        var step = new EnsureAuthorizedKeys();

        var result = await step.ExecuteAsync(context, dryRun: false, CancellationToken.None);

        Assert.Equal(ProvisioningStatus.Completed, result.Status);
        Assert.Equal(1, runner.KeyAddCount);
        Assert.Contains(result.Logs, log => log == "Public key added to authorized_keys.");
    }

    [Fact]
    public async Task Steps_MockGitCheck_ExecutionBehavior()
    {
        var runner = new MockCommandRunner();
        var checker = new MockCapabilityChecker();
        
        // Mock command: git --version
        runner.CommandHandler = (cmd) => 
        {
            if (cmd.Contains("git --version"))
            {
                return new CommandResult(0, "git version 2.40.1.windows.1", string.Empty);
            }
            return new CommandResult(-1, string.Empty, "Not found");
        };

        var step = new EnsureGitInstalled();
        var config = new ProvisioningConfig
        {
            AuthorizedPublicKey = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI123456789",
            InstallGit = true
        };
        
        var caps = await checker.CheckCapabilitiesAsync();
        var context = new ProvisioningContext(runner, config, caps);

        var result = await step.ExecuteAsync(context, dryRun: false, CancellationToken.None);

        Assert.Equal(ProvisioningStatus.Completed, result.Status);
        Assert.Contains("Git is already installed", result.Logs[1]);
    }

    private static bool IsPrivilegedProvisioningCommand(string command)
    {
        return command.Contains("Get-WindowsCapability", StringComparison.OrdinalIgnoreCase)
            || command.Contains("Add-WindowsCapability", StringComparison.OrdinalIgnoreCase)
            || command.Contains("Set-Service", StringComparison.OrdinalIgnoreCase)
            || command.Contains("Start-Service", StringComparison.OrdinalIgnoreCase)
            || command.Contains("New-NetFirewallRule", StringComparison.OrdinalIgnoreCase)
            || command.Contains("Get-NetFirewallRule", StringComparison.OrdinalIgnoreCase)
            || command.Contains("powercfg", StringComparison.OrdinalIgnoreCase)
            || command.Contains("authorized_keys", StringComparison.OrdinalIgnoreCase)
            || command.Contains("Set-Acl", StringComparison.OrdinalIgnoreCase)
            || command.Contains("Add-Content", StringComparison.OrdinalIgnoreCase);
    }
}
