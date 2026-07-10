using System.Threading;
using System.Threading.Tasks;
using SimBootstrap.Contracts;

namespace SimBootstrap.Engine.Provisioning;

public class ProvisioningContext
{
    public ICommandRunner CommandRunner { get; }
    public ProvisioningConfig Config { get; }
    public WindowsCapabilities Capabilities { get; }

    public ProvisioningContext(ICommandRunner commandRunner, ProvisioningConfig config, WindowsCapabilities capabilities)
    {
        CommandRunner = commandRunner;
        Config = config;
        Capabilities = capabilities;
    }
}

public interface IProvisioningStep
{
    string Name { get; }
    string Description { get; }
    bool IsCritical { get; }
    Task<bool> ShouldRunAsync(ProvisioningContext context, CancellationToken cancellationToken);
    Task<ProvisioningStepResult> ExecuteAsync(ProvisioningContext context, bool dryRun, CancellationToken cancellationToken);
}
