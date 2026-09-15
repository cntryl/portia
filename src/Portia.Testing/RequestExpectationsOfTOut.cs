namespace Cntryl.Portia.Testing;

/// <summary>Lifecycle expectations for a request with a result. Each <c>Expect…</c> returns new expectations; awaiting runs the request once.</summary>
/// <typeparam name="TOut">The type of the value on success.</typeparam>
public sealed class RequestExpectations<TOut>
{
    readonly ScenarioDefinition<Result<TOut>> _definition;
    readonly Func<ScenarioObservation, Result<TOut>, string?>[] _expectations;
    readonly Lazy<Task<Result<TOut>>> _run;

    internal RequestExpectations(ScenarioDefinition<Result<TOut>> definition,
        Func<ScenarioObservation, Result<TOut>, string?>[] expectations)
    {
        _definition = definition;
        _expectations = expectations;
        _run = new Lazy<Task<Result<TOut>>>(() => ScenarioEngine.RunAsync(_definition, _expectations),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Expects authorization to have run and allowed the request.</summary>
    /// <returns>New expectations that include this one.</returns>
    public RequestExpectations<TOut> ExpectAuthorized() => With((observation, _) => ScenarioExpectations.Authorized(observation));

    /// <summary>Expects authorization to have denied the request before any behavior, guard, or handler ran.</summary>
    /// <param name="kind">The expected error kind, or <see langword="null" /> for any denial.</param>
    /// <returns>New expectations that include this one.</returns>
    public RequestExpectations<TOut> ExpectDenied(RequestErrorKind? kind = null) =>
        With((observation, _) => ScenarioExpectations.Denied(observation, kind));

    /// <summary>Expects <typeparamref name="TGuard" /> to have run and passed.</summary>
    /// <typeparam name="TGuard">A guard registered for the request.</typeparam>
    /// <returns>New expectations that include this one.</returns>
    public RequestExpectations<TOut> ExpectGuardPassed<TGuard>() where TGuard : class =>
        With((observation, _) => ScenarioExpectations.GuardPassed(observation, typeof(TGuard)));

    /// <summary>Expects <typeparamref name="TGuard" /> to have run and failed, stopping the request.</summary>
    /// <typeparam name="TGuard">A guard registered for the request.</typeparam>
    /// <param name="kind">The expected error kind, or <see langword="null" /> for any failure.</param>
    /// <returns>New expectations that include this one.</returns>
    public RequestExpectations<TOut> ExpectGuardFailed<TGuard>(RequestErrorKind? kind = null) where TGuard : class =>
        With((observation, _) => ScenarioExpectations.GuardFailed(observation, typeof(TGuard), kind));

    /// <summary>Expects the request's handler to have run.</summary>
    /// <returns>New expectations that include this one.</returns>
    public RequestExpectations<TOut> ExpectHandled() => With((observation, _) => ScenarioExpectations.Handled(observation));

    /// <summary>Expects the request's handler not to have run.</summary>
    /// <returns>New expectations that include this one.</returns>
    public RequestExpectations<TOut> ExpectNotHandled() => With((observation, _) => ScenarioExpectations.NotHandled(observation));

    /// <summary>Expects the request to have succeeded.</summary>
    /// <returns>New expectations that include this one.</returns>
    public RequestExpectations<TOut> ExpectSuccess() => With((observation, _) => ScenarioExpectations.Success(observation));

    /// <summary>Expects the request to have failed.</summary>
    /// <param name="kind">The expected error kind, or <see langword="null" /> for any failure.</param>
    /// <returns>New expectations that include this one.</returns>
    public RequestExpectations<TOut> ExpectFailure(RequestErrorKind? kind = null) =>
        With((observation, _) => ScenarioExpectations.Failure(observation, kind));

    /// <summary>Expects the request to have succeeded with <paramref name="expected" />.</summary>
    /// <param name="expected">The expected value, compared with <see cref="EqualityComparer{T}.Default" />.</param>
    /// <returns>New expectations that include this one.</returns>
    public RequestExpectations<TOut> ExpectSuccess(TOut expected) => With((observation, result) =>
        ScenarioExpectations.Success(observation) ?? (EqualityComparer<TOut>.Default.Equals(result.Value, expected)
            ? null
            : $"expected success with value {expected}, but the value was {result.Value}"));

    /// <summary>Runs the request once and asserts every expectation.</summary>
    /// <returns>The request's result.</returns>
    /// <exception cref="ScenarioExpectationException">One or more expectations were not met.</exception>
    public Task<Result<TOut>> AsTask() => _run.Value;

    /// <summary>Runs the request once and asserts every expectation when awaited.</summary>
    /// <returns>An awaiter for <see cref="AsTask" />.</returns>
    public System.Runtime.CompilerServices.TaskAwaiter<Result<TOut>> GetAwaiter() => AsTask().GetAwaiter();

    RequestExpectations<TOut> With(Func<ScenarioObservation, Result<TOut>, string?> expectation) =>
        new(_definition, [.. _expectations, expectation]);
}
