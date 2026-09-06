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
    internal WorkloadScope? Scope { get; private set; }
    /// <summary>Gets or sets a stable application name; defaults to the component's full type name.</summary>
    public string? Name { get; set; }
    /// <summary>Gets or sets the delay between completed passes.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Gets or sets projection/reaction batch limits and rebuild settings.</summary>
    public ProjectionRunOptions Processing { get; set; } = ProjectionRunOptions.Default;

    /// <summary>Selects one logical application workload.</summary>
    public WorkloadOptions Global() => Select(WorkloadScope.Global);
    /// <summary>Selects independently owned and checkpointed tenant workloads.</summary>
    public WorkloadOptions PerTenant() => Select(WorkloadScope.PerTenant);
    WorkloadOptions Select(WorkloadScope scope)
    {
        if (Scope is { } existing && existing != scope)
            throw new InvalidOperationException("A workload cannot be both global and per-tenant.");
        Scope = scope;
        return this;
    }
}

/// <summary>An immutable application workload declaration, independent of infrastructure.</summary>
public sealed record WorkloadRegistration
{
    internal WorkloadRegistration(Type componentType, bool projector, Action<WorkloadOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new WorkloadOptions();
        configure(options);
        Scope = options.Scope ?? throw new InvalidOperationException("Choose Global() or PerTenant() for every workload.");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.PollInterval, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(options.Processing);
        options.Processing.Validate();
        Name = options.Name ?? componentType.FullName ?? componentType.Name;
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        if (!projector && options.Processing.RebuildId is not null)
            throw new ArgumentException("Reactors do not support projection rebuild generations.", nameof(configure));
        ComponentType = componentType;
        IsProjector = projector;
        PollInterval = options.PollInterval;
        Processing = options.Processing;
    }

    /// <summary>Gets the explicitly selected component type.</summary>
    public Type ComponentType { get; }
    /// <summary>Gets whether the component is a projector.</summary>
    public bool IsProjector { get; }
    /// <summary>Gets the execution scope.</summary>
    public WorkloadScope Scope { get; }
    /// <summary>Gets the stable logical name.</summary>
    public string Name { get; }
    /// <summary>Gets the delay between passes.</summary>
    public TimeSpan PollInterval { get; }
    /// <summary>Gets batch processing settings.</summary>
    public ProjectionRunOptions Processing { get; }
}
