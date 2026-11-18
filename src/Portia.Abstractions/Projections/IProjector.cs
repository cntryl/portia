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
    static Func<T, string, EventStreamPattern, Projector>? _factory;

    internal static IReadOnlyList<string> Subscriptions { get; private set; } = [];

    /// <summary>Installs the factory emitted by the Portia generator.</summary>
    /// <param name="factory">Constructs the scoped dispatch adapter.</param>
    /// <param name="subscriptions">The event types selected by the adapter.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000", Justification = "The generated factory is stored per closed projector type.")]
    public static void Register(Func<T, string, EventStreamPattern, Projector> factory, params string[] subscriptions)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (Interlocked.CompareExchange(ref _factory, factory, null) is not null)
            throw new InvalidOperationException($"Dispatch already registered for {typeof(T)}.");
        Subscriptions = Array.AsReadOnly(subscriptions);
    }

    internal static Projector Create(T projector, string name, EventStreamPattern source)
    {
        var adapter = (_factory ?? throw new InvalidOperationException($"Missing generated dispatch for {typeof(T)}."))(
            projector, name, source);
        adapter.ApplicationType = typeof(T);
        return adapter;
    }
}
