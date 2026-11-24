using System;

namespace Cntryl.Portia.Secrets;

/// <summary>Reports that a provider could not supply the current secret.</summary>
public sealed class SecretUnavailableException : Exception
{
    /// <summary>Creates a failure without including secret or provider details.</summary>
    public SecretUnavailableException() : base("The current secret is unavailable.")
    {
    }
}
