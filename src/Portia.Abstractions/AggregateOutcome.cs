namespace Cntryl.Portia;

/// <summary>
///     The handler's decision for one aggregate operation: the result the caller receives and, independently,
///     whether everything the operation produced is committed or discarded. All four combinations are valid:
///     a denied login commits its audit, and an idempotent no-op discards.
/// </summary>
public readonly struct AggregateOutcome
{
    AggregateOutcome(Result result, AggregateDisposition disposition)
    {
        Result = result;
        Disposition = disposition;
    }

    /// <summary>Gets the result the caller receives.</summary>
    public Result Result { get; }

    /// <summary>Gets whether the operation's pending records are committed or discarded.</summary>
    public AggregateDisposition Disposition { get; }

    /// <summary>Commits everything the operation produced and returns <paramref name="result" />.</summary>
    /// <param name="result">The result the caller receives.</param>
    /// <returns>The outcome.</returns>
    public static AggregateOutcome Commit(Result result) => new(Initialized(result), AggregateDisposition.Commit);

    /// <summary>Discards everything the operation produced and returns <paramref name="result" />.</summary>
    /// <param name="result">The result the caller receives.</param>
    /// <returns>The outcome.</returns>
    public static AggregateOutcome Discard(Result result) => new(Initialized(result), AggregateDisposition.Discard);

    /// <summary>Commits a successful result and discards a failed result.</summary>
    /// <param name="result">The result the caller receives.</param>
    /// <returns>The outcome.</returns>
    /// <remarks>
    ///     Use <see cref="Commit(Result)" /> explicitly when a failed operation produced records, such as an audit,
    ///     that must still be persisted.
    /// </remarks>
    public static AggregateOutcome CommitOnSuccess(Result result)
    {
        var initialized = Initialized(result);
        return new(initialized,
            initialized.IsSuccess ? AggregateDisposition.Commit : AggregateDisposition.Discard);
    }

    /// <summary>Commits everything the operation produced and returns <paramref name="result" />.</summary>
    /// <typeparam name="TOut">The type of the value on success.</typeparam>
    /// <param name="result">The result the caller receives.</param>
    /// <returns>The outcome.</returns>
    public static AggregateOutcome<TOut> Commit<TOut>(Result<TOut> result) =>
        new(Initialized(result), AggregateDisposition.Commit);

    /// <summary>Discards everything the operation produced and returns <paramref name="result" />.</summary>
    /// <typeparam name="TOut">The type of the value on success.</typeparam>
    /// <param name="result">The result the caller receives.</param>
    /// <returns>The outcome.</returns>
    public static AggregateOutcome<TOut> Discard<TOut>(Result<TOut> result) =>
        new(Initialized(result), AggregateDisposition.Discard);

    /// <summary>Commits a successful result and discards a failed result.</summary>
    /// <typeparam name="TOut">The type of the value on success.</typeparam>
    /// <param name="result">The result the caller receives.</param>
    /// <returns>The outcome.</returns>
    /// <remarks>
    ///     Use <see cref="Commit{TOut}(Result{TOut})" /> explicitly when a failed operation produced records,
    ///     such as an audit, that must still be persisted.
    /// </remarks>
    public static AggregateOutcome<TOut> CommitOnSuccess<TOut>(Result<TOut> result)
    {
        var initialized = Initialized(result);
        return new(initialized,
            initialized.IsSuccess ? AggregateDisposition.Commit : AggregateDisposition.Discard);
    }

    static Result Initialized(Result result)
    {
        try
        {
            _ = result.IsSuccess;
            return result;
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException("An aggregate outcome needs an initialized Result.", nameof(result), ex);
        }
    }

    static Result<TOut> Initialized<TOut>(Result<TOut> result)
    {
        try
        {
            _ = result.IsSuccess;
            return result;
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException("An aggregate outcome needs an initialized Result.", nameof(result), ex);
        }
    }
}

/// <summary>
///     The handler's decision for one value-returning aggregate operation: the result the caller receives and,
///     independently, whether everything the operation produced is committed or discarded. Create it with
///     <see cref="AggregateOutcome.Commit{TOut}(Result{TOut})" /> or <see cref="AggregateOutcome.Discard{TOut}(Result{TOut})" />.
/// </summary>
/// <typeparam name="TOut">The type of the value on success.</typeparam>
public readonly struct AggregateOutcome<TOut>
{
    internal AggregateOutcome(Result<TOut> result, AggregateDisposition disposition)
    {
        Result = result;
        Disposition = disposition;
    }

    /// <summary>Gets the result the caller receives.</summary>
    public Result<TOut> Result { get; }

    /// <summary>Gets whether the operation's pending records are committed or discarded.</summary>
    public AggregateDisposition Disposition { get; }
}
