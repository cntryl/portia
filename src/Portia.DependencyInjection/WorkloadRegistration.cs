namespace Cntryl.Portia;

/// <summary>The independent execution scope of a registered component.</summary>
public enum WorkloadScope
{
    /// <summary>One logical workload for the application.</summary>
    Global,
    /// <summary>One logical workload per active tenant.</summary>
    PerTenant,
}

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

/// <summary>An immutable application workload declaration, independent of infrastructure.</summary>
public sealed record WorkloadRegistration
{
    internal WorkloadRegistration(
        IWorkloadDescriptor descriptor,
        WorkloadScope scope,
        Action<WorkloadOptions>? configure)
    {
        if (!Enum.IsDefined(scope))
            throw new ArgumentOutOfRangeException(nameof(scope), scope, "Choose a defined workload scope.");
        var options = new WorkloadOptions();
        configure?.Invoke(options);
        Scope = scope;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.PollInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.FailureAttemptLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumFailureDelay, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(options.Processing);
        options.Processing.Validate();
        ExplicitName = options.Name;
        Name = options.Name ?? descriptor.ComponentType.FullName ?? descriptor.ComponentType.Name;
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        // Asked of the descriptor rather than tested against a known descriptor type, so a
        // component kind added later answers for itself instead of silently gaining rebuilds.
        if (!descriptor.SupportsRebuild && options.Processing.RebuildId is not null)
            throw new ArgumentException("Reactors do not support projection rebuild generations.", nameof(configure));
        Descriptor = descriptor;
        ComponentType = descriptor.ComponentType;
        PollInterval = options.PollInterval;
        FailureAttemptLimit = options.FailureAttemptLimit;
        MaximumFailureDelay = options.MaximumFailureDelay;
        Processing = options.Processing;
    }

    /// <summary>Gets the explicitly selected component type.</summary>
    public Type ComponentType { get; }
    internal IWorkloadDescriptor Descriptor { get; }
    /// <summary>Gets the execution scope.</summary>
    public WorkloadScope Scope { get; }
    /// <summary>Gets the stable logical name used to coordinate ownership of this workload.</summary>
    public string Name { get; }
    /// <summary>Gets the name the application chose, or null to keep the component's own name
    /// — which is its checkpoint identity — exactly as its constructor set it.</summary>
    internal string? ExplicitName { get; }
    /// <summary>Gets the delay between passes.</summary>
    public TimeSpan PollInterval { get; }
    /// <summary>Gets the consecutive failed-pass limit before the hosted worker faults.</summary>
    public int FailureAttemptLimit { get; }
    /// <summary>Gets the maximum delay between failed-pass attempts.</summary>
    public TimeSpan MaximumFailureDelay { get; }
    /// <summary>Gets batch processing settings.</summary>
    public ProjectionRunOptions Processing { get; }
}
