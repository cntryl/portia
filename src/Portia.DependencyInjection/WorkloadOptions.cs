namespace Cntryl.Portia;

/// <summary>Configures one explicit component registration.</summary>
public sealed class WorkloadOptions
{
    /// <summary>Gets or sets a stable application name; defaults to the component's full type name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the delay between completed passes.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets projection/reaction batch limits and rebuild settings.</summary>
    public ProjectionRunOptions Processing { get; set; } = ProjectionRunOptions.Default;

    /// <summary>Gets or sets the consecutive failed-pass limit before the hosted worker faults.</summary>
    public int FailureAttemptLimit { get; set; } = 10;

    /// <summary>Gets or sets the maximum delay between failed-pass attempts.</summary>
    public TimeSpan MaximumFailureDelay { get; set; } = TimeSpan.FromMinutes(1);
}
