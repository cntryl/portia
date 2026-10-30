namespace Cntryl.Portia;

/// <summary>Metadata-only observation hooks used by generated dispatch; never an authorization source.</summary>
public static class ProcessingObservation
{
    static readonly AsyncLocal<Action<string, string, string?, string?, string>?> Observer = new();

    internal static IDisposable Capture(Action<string, string, string?, string?, string> observe)
    {
        var previous = Observer.Value;
        Observer.Value = observe;
        return new CaptureScope(previous);
    }

    internal static bool IsEnabled => Observer.Value is not null;

    /// <summary>Observes a generated single-event projector invocation without allocating a dispatch closure.</summary>
    public static ValueTask<Result> InvokeAsync<TEvent>(string handlerName, IProjectorHandler<TEvent> handler,
        IProjectorContext<TEvent> context, CancellationToken ct) where TEvent : DomainEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (Observer.Value is not { } observe)
            return handler.HandleAsync(context, ct);
        var source = context.Source.Event.Metadata.EventId.ToString();
        observe("handler", handlerName, source, null, "started");
        try
        { return CompleteAsync(handler.HandleAsync(context, ct), observe, handlerName, source); }
        catch { observe("handler", handlerName, source, null, "fault"); throw; }
    }

    /// <summary>Observes a generated single-event reactor invocation without allocating a dispatch closure.</summary>
    public static ValueTask<Result> InvokeAsync<TEvent>(string handlerName, IReactorHandler<TEvent> handler,
        IReactorContext<TEvent> context, CancellationToken ct) where TEvent : DomainEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (Observer.Value is not { } observe)
            return handler.HandleAsync(context, ct);
        var source = context.Source.Event.Metadata.EventId.ToString();
        observe("handler", handlerName, source, null, "started");
        try
        { return CompleteAsync(handler.HandleAsync(context, ct), observe, handlerName, source); }
        catch { observe("handler", handlerName, source, null, "fault"); throw; }
    }

    /// <summary>Observes a generated batch projector invocation without allocating a dispatch closure.</summary>
    public static ValueTask<Result> InvokeAsync<TEvent>(string handlerName, IBatchProjectorHandler<TEvent> handler,
        IReadOnlyList<IProjectorContext<TEvent>> contexts, CancellationToken ct) where TEvent : DomainEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (Observer.Value is not { } observe)
            return handler.HandleAsync(contexts, ct);
        var source = contexts[0].Source.Event.Metadata.EventId.ToString();
        observe("handler", handlerName, source, null, "started");
        foreach (var context in contexts)
            observe("handler-source", handlerName, context.Source.Event.Metadata.EventId.ToString(), null, "included");
        try
        { return CompleteAsync(handler.HandleAsync(contexts, ct), observe, handlerName, source); }
        catch { observe("handler", handlerName, source, null, "fault"); throw; }
    }

    /// <summary>Observes a generated batch reactor invocation without allocating a dispatch closure.</summary>
    public static ValueTask<Result> InvokeAsync<TEvent>(string handlerName, IBatchReactorHandler<TEvent> handler,
        IReadOnlyList<IReactorContext<TEvent>> contexts, CancellationToken ct) where TEvent : DomainEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (Observer.Value is not { } observe)
            return handler.HandleAsync(contexts, ct);
        var source = contexts[0].Source.Event.Metadata.EventId.ToString();
        observe("handler", handlerName, source, null, "started");
        foreach (var context in contexts)
            observe("handler-source", handlerName, context.Source.Event.Metadata.EventId.ToString(), context.ExecutionId.ToString(), "included");
        try
        { return CompleteAsync(handler.HandleAsync(contexts, ct), observe, handlerName, source); }
        catch { observe("handler", handlerName, source, null, "fault"); throw; }
    }

    static async ValueTask<Result> CompleteAsync(ValueTask<Result> pending,
        Action<string, string, string?, string?, string> observe, string handler, string source)
    {
        try
        {
            var result = await pending.ConfigureAwait(false);
            observe("handler", handler, source, null, result.IsSuccess ? "success" : result.Error.Kind.ToString());
            return result;
        }
        catch
        {
            observe("handler", handler, source, null, "fault");
            throw;
        }
    }

    internal static void Record(string kind, string component, string? identity, string? execution, string outcome)
        => Observer.Value?.Invoke(kind, component, identity, execution, outcome);

    sealed class CaptureScope(Action<string, string, string?, string?, string>? previous) : IDisposable
    {
        public void Dispose() => Observer.Value = previous;
    }
}
