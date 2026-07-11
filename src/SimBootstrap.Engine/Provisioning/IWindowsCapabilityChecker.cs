using System.Threading;
using System.Threading.Tasks;

namespace SimBootstrap.Engine.Provisioning;

public class WindowsCapabilities
{
    public string WindowsVersion { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public string PowerShellVersion { get; set; } = string.Empty;
    public bool IsWinGetAvailable { get; set; }
}

public interface IWindowsCapabilityChecker
{
    Task<WindowsCapabilities> CheckCapabilitiesAsync(CancellationToken cancellationToken = default);
}
