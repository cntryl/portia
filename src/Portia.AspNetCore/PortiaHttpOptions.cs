namespace Cntryl.Portia;

/// <summary>Controls Portia's generated HTTP boundary.</summary>
public sealed class PortiaHttpOptions
{
    /// <summary>Gets the default maximum JSON request-body size (10 MiB).</summary>
    public const long DefaultMaxJsonBodyBytes = 10 * 1024 * 1024;

    /// <summary>Gets or sets the maximum JSON request-body size.</summary>
    public long MaxJsonBodyBytes { get; set; } = DefaultMaxJsonBodyBytes;
}
