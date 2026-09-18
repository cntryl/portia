namespace Cntryl.Portia;

/// <summary>
///     One activated Fitz listener. Each kind owns its route shape, the services it requires at
///     startup, and how it builds its own runner, so hosting enumerates definitions without asking
///     what kind any of them is.
/// </summary>
abstract record FitzWorkerDefinition(string Route)
{
    internal abstract string Key { get; }
    internal abstract IReadOnlyCollection<Type> Requirements { get; }

    /// <summary>The background pass to run, or null when this kind is started elsewhere.</summary>
    internal virtual Func<CancellationToken, Task>? CreateRunner(FitzWorkerHost host) => null;
}
