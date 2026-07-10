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

    private class MockCapabilityChecker : IWindowsCapabilityChecker
    {
        public WindowsCapabilities Capabilities { get; set; } = new()
        {
            WindowsVersion = "Windows 11 Pro Mock",
            IsAdmin = true,
            PowerShellVersion = "7.4.0",
            IsWinGetAvailable = true
        };

        public Task<WindowsCapabilities> CheckCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
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
}
