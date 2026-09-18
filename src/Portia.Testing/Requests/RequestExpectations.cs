using System.Runtime.CompilerServices;

namespace Cntryl.Portia.Testing;

/// <summary>
///     Lifecycle expectations for a request with no result. Each <c>Expect…</c> returns new expectations; awaiting
///     runs the request once.
/// </summary>
public sealed class RequestExpectations
{
    readonly ScenarioDefinition<Result> _definition;
    readonly Func<ScenarioObservation, Result, string?>[] _expectations;
    readonly Lazy<Task<Result>> _run;

    internal RequestExpectations(ScenarioDefinition<Result> definition,
        Func<ScenarioObservation, Result, string?>[] expectations)
    {
        _definition = definition;
        _expectations = expectations;
        _run = new Lazy<Task<Result>>(() => ScenarioEngine.RunAsync(_definition, _expectations),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Expects authorization to have run and allowed the request.</summary>
    /// <returns>New expectations that include this one.</returns>
    public RequestExpectations ExpectAuthorized() =>
        With((observation, _) => ScenarioExpectations.Authorized(observation));

    /// <summary>Expects authorization to have denied the request before any behavior, guard, or handler ran.</summary>
    /// <param name="kind">The expected error kind, or <see langword="null" /> for any denial.</param>
    /// <returns>New expectations that include this one.</returns>
    public RequestExpectations ExpectDenied(RequestErrorKind? kind = null) =>
        With((observation, _) => ScenarioExpectations.Denied(observation, kind));

    /// <summary>Expects <typeparamref name="TGuard" /> to have run and passed.</summary>
    /// <typeparam name="TGuard">A guard registered for the request.</typeparam>
    /// <returns>New expectations that include this one.</returns>
    public RequestExpectations ExpectGuardPassed<TGuard>() where TGuard : class =>
        With((observation, _) => ScenarioExpectations.GuardPassed(observation, typeof(TGuard)));

    /// <summary>Expects <typeparamref name="TGuard" /> to have run and failed, stopping the request.</summary>
    /// <typeparam name="TGuard">A guard registered for the request.</typeparam>
    /// <param name="kind">The expected error kind, or <see langword="null" /> for any failure.</param>
    /// <returns>New expectations that include this one.</returns>
    public RequestExpectations ExpectGuardFailed<TGuard>(RequestErrorKind? kind = null) where TGuard : class =>
        With((observation, _) => ScenarioExpectations.GuardFailed(observation, typeof(TGuard), kind));

    /// <summary>Expects the request's handler to have run.</summary>
    /// <returns>New expectations that include this one.</returns>
    public RequestExpectations ExpectHandled() => With((observation, _) => ScenarioExpectations.Handled(observation));

    /// <summary>Expects the request's handler not to have run.</summary>
    /// <returns>New expectations that include this one.</returns>
    public RequestExpectations ExpectNotHandled() =>
        With((observation, _) => ScenarioExpectations.NotHandled(observation));

    /// <summary>Expects the request to have succeeded.</summary>
    /// <returns>New expectations that include this one.</returns>
    public RequestExpectations ExpectSuccess() => With((observation, _) => ScenarioExpectations.Success(observation));

    /// <summary>Expects the request to have failed.</summary>
    /// <param name="kind">The expected error kind, or <see langword="null" /> for any failure.</param>
    /// <returns>New expectations that include this one.</returns>
    public RequestExpectations ExpectFailure(RequestErrorKind? kind = null) =>
        With((observation, _) => ScenarioExpectations.Failure(observation, kind));

    /// <summary>Runs the request once and asserts every expectation.</summary>
    /// <returns>The request's result.</returns>
    /// <exception cref="ScenarioExpectationException">One or more expectations were not met.</exception>
    public Task<Result> AsTask() => _run.Value;

    /// <summary>Runs the request once and asserts every expectation when awaited.</summary>
    /// <returns>An awaiter for <see cref="AsTask" />.</returns>
    public TaskAwaiter<Result> GetAwaiter() => AsTask().GetAwaiter();

    RequestExpectations With(Func<ScenarioObservation, Result, string?> expectation) =>
        new(_definition, [.. _expectations, expectation]);
}
