namespace Cntryl.Portia;

/// <summary>Declares the repository used by all of a projector's event handlers.</summary>
public interface IProjector
{
    /// <summary>Gets the repository whose transaction owns the data and checkpoint.</summary>
    IProjectionStore Store { get; }
}

/// <summary>Connects generated typed dispatch to an application projector.</summary>
/// <typeparam name="T">The application projector type.</typeparam>
public static class ProjectorDispatch<T> where T : class, IProjector
{
    sealed record Dispatch(Func<T, string, EventStreamPattern, Projector> Factory, IReadOnlyList<string> Subscriptions);

    static Dispatch? Registered;

    static Dispatch Required => Volatile.Read(ref Registered) ?? throw new InvalidOperationException(
        typeof(Projector).IsAssignableFrom(typeof(T))
            ? $"{typeof(T)} derives from Projector and declares its own source. Use the WorkloadScope registration overload or pass the constructed projector to the scenario."
            : $"Missing generated dispatch for {typeof(T)}. Implement a typed projector handler and reference the Portia generator.");

    internal static IReadOnlyList<string> Subscriptions => Required.Subscriptions;

    /// <summary>Installs the factory emitted by the Portia generator.</summary>
    /// <param name="factory">Constructs the scoped dispatch adapter.</param>
    /// <param name="subscriptions">The event types selected by the adapter.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000", Justification = "The generated factory is stored per closed projector type.")]
    public static void Register(Func<T, string, EventStreamPattern, Projector> factory, params string[] subscriptions)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var dispatch = new Dispatch(factory, Array.AsReadOnly(subscriptions));
        if (Interlocked.CompareExchange(ref Registered, dispatch, null) is not null)
            throw new InvalidOperationException($"Dispatch already registered for {typeof(T)}.");
    }

    internal static Projector Create(T projector, string name, EventStreamPattern source)
    {
        var adapter = Required.Factory(projector, name, source);
        adapter.ApplicationType = typeof(T);
        return adapter;
    }
}
