using System.Runtime.CompilerServices;

namespace Cntryl.Portia.Testing;

/// <summary>
///     Lifecycle expectations for a streamed request. Each <c>Expect…</c> returns new expectations; awaiting runs the
///     request once and collects every item.
/// </summary>
/// <typeparam name="TOut">The type of each item.</typeparam>
public sealed class StreamRequestExpectations<TOut>
{
    readonly ScenarioDefinition<IReadOnlyList<TOut>> _definition;
    readonly Func<ScenarioObservation, IReadOnlyList<TOut>, string?>[] _expectations;
    readonly Lazy<Task<IReadOnlyList<TOut>>> _run;

    internal StreamRequestExpectations(ScenarioDefinition<IReadOnlyList<TOut>> definition,
        Func<ScenarioObservation, IReadOnlyList<TOut>, string?>[] expectations)
    {
        _definition = definition;
        _expectations = expectations;
        _run = new Lazy<Task<IReadOnlyList<TOut>>>(() => ScenarioEngine.RunAsync(_definition, _expectations),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Expects authorization to have run and allowed the request.</summary>
    /// <returns>New expectations that include this one.</returns>
    public StreamRequestExpectations<TOut> ExpectAuthorized() =>
        With((observation, _) => ScenarioExpectations.Authorized(observation));

    /// <summary>Expects authorization to have denied the request before any behavior, guard, or handler ran.</summary>
    /// <param name="kind">The expected error kind, or <see langword="null" /> for any denial.</param>
    /// <returns>New expectations that include this one.</returns>
    public StreamRequestExpectations<TOut> ExpectDenied(RequestErrorKind? kind = null) =>
        With((observation, _) => ScenarioExpectations.Denied(observation, kind));

    /// <summary>Expects <typeparamref name="TGuard" /> to have run and passed.</summary>
    /// <typeparam name="TGuard">A guard registered for the request.</typeparam>
    /// <returns>New expectations that include this one.</returns>
    public StreamRequestExpectations<TOut> ExpectGuardPassed<TGuard>() where TGuard : class =>
        With((observation, _) => ScenarioExpectations.GuardPassed(observation, typeof(TGuard)));

    /// <summary>Expects <typeparamref name="TGuard" /> to have run and failed, stopping the request.</summary>
    /// <typeparam name="TGuard">A guard registered for the request.</typeparam>
    /// <param name="kind">The expected error kind, or <see langword="null" /> for any failure.</param>
    /// <returns>New expectations that include this one.</returns>
    public StreamRequestExpectations<TOut> ExpectGuardFailed<TGuard>(RequestErrorKind? kind = null)
        where TGuard : class =>
        With((observation, _) => ScenarioExpectations.GuardFailed(observation, typeof(TGuard), kind));

    /// <summary>Expects the request's handler to have run.</summary>
    /// <returns>New expectations that include this one.</returns>
    public StreamRequestExpectations<TOut> ExpectHandled() =>
        With((observation, _) => ScenarioExpectations.Handled(observation));

    /// <summary>Expects the request's handler not to have run.</summary>
    /// <returns>New expectations that include this one.</returns>
    public StreamRequestExpectations<TOut> ExpectNotHandled() =>
        With((observation, _) => ScenarioExpectations.NotHandled(observation));

    /// <summary>Expects the request to have succeeded.</summary>
    /// <returns>New expectations that include this one.</returns>
    public StreamRequestExpectations<TOut> ExpectSuccess() =>
        With((observation, _) => ScenarioExpectations.Success(observation));

    /// <summary>Expects the request to have failed.</summary>
    /// <param name="kind">The expected error kind, or <see langword="null" /> for any failure.</param>
    /// <returns>New expectations that include this one.</returns>
    public StreamRequestExpectations<TOut> ExpectFailure(RequestErrorKind? kind = null) =>
        With((observation, _) => ScenarioExpectations.Failure(observation, kind));

    /// <summary>Expects the stream to have produced exactly <paramref name="expected" />, in order.</summary>
    /// <param name="expected">The expected items, compared with <see cref="EqualityComparer{T}.Default" />.</param>
    /// <returns>New expectations that include this one.</returns>
    public StreamRequestExpectations<TOut> ExpectItems(params TOut[] expected) => With((_, items) =>
        items.SequenceEqual(expected)
            ? null
            : $"expected items [{string.Join(", ", expected)}], but the stream produced [{string.Join(", ", items)}]");

    /// <summary>Runs the request once and asserts every expectation.</summary>
    /// <returns>Every item the stream produced; empty when authorization or a guard stopped it.</returns>
    /// <exception cref="ScenarioExpectationException">One or more expectations were not met.</exception>
    public Task<IReadOnlyList<TOut>> AsTask() => _run.Value;

    /// <summary>Runs the request once and asserts every expectation when awaited.</summary>
    /// <returns>An awaiter for <see cref="AsTask" />.</returns>
    public TaskAwaiter<IReadOnlyList<TOut>> GetAwaiter() => AsTask().GetAwaiter();

    StreamRequestExpectations<TOut> With(Func<ScenarioObservation, IReadOnlyList<TOut>, string?> expectation) =>
        new(_definition, [.. _expectations, expectation]);
}
