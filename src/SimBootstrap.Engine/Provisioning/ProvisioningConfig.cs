using System;

namespace SimBootstrap.Engine.Provisioning;

public class ProvisioningConfig
{
    public string AuthorizedPublicKey { get; set; } = string.Empty;
    public bool InstallGit { get; set; }
    public bool InstallDotNet9 { get; set; }
    public bool ConfigureOpenSsh { get; set; }
    public bool ConfigureFirewall { get; set; }
    public bool DisableSleep { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(AuthorizedPublicKey))
        {
            throw new ArgumentException("AuthorizedPublicKey is required and cannot be empty.");
        }
        
        var trimmed = AuthorizedPublicKey.Trim();
        if (!trimmed.StartsWith("ssh-rsa ", StringComparison.OrdinalIgnoreCase) && 
            !trimmed.StartsWith("ssh-ed25519 ", StringComparison.OrdinalIgnoreCase) && 
            !trimmed.StartsWith("ecdsa-sha2 ", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("AuthorizedPublicKey must start with a valid SSH key type (ssh-rsa, ssh-ed25519, or ecdsa-sha2) followed by a space.");
        }
    }
}
