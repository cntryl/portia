using System.Security.Cryptography;

namespace Cntryl.Portia.DataProtection;

/// <summary>Reports that the provider could not supply a required key or current snapshot.</summary>
public sealed class DataProtectionKeyUnavailableException : CryptographicException
{
    /// <summary>Creates a provider failure without including sensitive provider details.</summary>
    public DataProtectionKeyUnavailableException() : base("The data protection key ring is unavailable.")
    {
    }
}
