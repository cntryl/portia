namespace Cntryl.Portia;

/// <summary>An immutable application workload declaration, independent of infrastructure.</summary>
public sealed record WorkloadRegistration
{
    internal WorkloadRegistration(
        IWorkloadDescriptor descriptor,
        string name,
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
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        if (!descriptor.SupportsRebuild &&
            // Asked of the descriptor rather than tested against a known descriptor type, so a
            // component kind added later answers for itself instead of silently gaining rebuilds.
            options.Processing.RebuildId is not null)
        {
            throw new ArgumentException("Reactors do not support projection rebuild generations.", nameof(configure));
        }

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

    /// <summary>
    ///     Gets the stable hosted identity used for ownership, checkpoints, and reactor effects.
    ///     Constructor-selected component names apply only when a component is driven manually.
    /// </summary>
    public string Name { get; }

    /// <summary>Gets the delay between passes.</summary>
    public TimeSpan PollInterval { get; }

    /// <summary>Gets the consecutive failed-pass limit before the hosted worker faults.</summary>
    public int FailureAttemptLimit { get; }

    /// <summary>Gets the maximum delay between failed-pass attempts.</summary>
    public TimeSpan MaximumFailureDelay { get; }

    /// <summary>Gets batch processing settings.</summary>
    public ProjectionRunOptions Processing { get; }
}
