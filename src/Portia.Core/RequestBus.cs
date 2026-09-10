using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Dispatches through generated descriptors, resolving only the selected dependencies in the current scope.</summary>
/// <param name="services">The current application scope.</param>
/// <param name="registry">The composed generated registrations.</param>
public sealed class RequestBus(IServiceProvider services, RequestRegistry registry) : IRequestBus
{
    /// <inheritdoc />
    public RequestDispatchContext CreateContext(ClaimsPrincipal actor, RequestMetadata? metadata = null)
        => new(actor, metadata: metadata, timeProvider: services.GetService<TimeProvider>());

    /// <inheritdoc />
    public async ValueTask<Result> DispatchAsync(IRequest request, RequestDispatchContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Validate(request, context, ct);
        var registration = registry.Handler(request.GetType());
        var policies = registry.Policies(registration.RequestType);
        using var activity = PortiaTelemetry.StartExecute(policies.Name);
        var started = PortiaTelemetry.StartTimestamp();
        var transport = context.Invocation.TransportName;
        PortiaTelemetry.RequestStarted(policies.Name, transport);
        var outcome = "fault";
        var completed = false;
        try
        {
            var authorization = await AuthorizeAsync(registration, policies, request, context, ct).ConfigureAwait(false);
            var result = authorization.IsSuccess
                ? await InvokeAsync(registration, policies, request, context, ct).ConfigureAwait(false)
                : authorization;
            PortiaTelemetry.RecordOutcome(activity, result.IsSuccess, result.Error);
            outcome = PortiaTelemetry.Outcome(result.IsSuccess, result.Error);
            completed = true;
            return result;
        }
        catch (OperationCanceledException) { _ = activity?.SetStatus(ActivityStatusCode.Error); throw; }
        catch (Exception ex) { _ = activity?.AddException(ex); _ = activity?.SetStatus(ActivityStatusCode.Error); throw; }
        finally { PortiaTelemetry.RequestFinished(started, policies.Name, transport, Finish(completed, outcome, ct)); }
    }

    /// <inheritdoc />
    public async ValueTask<Result<TOut>> DispatchAsync<TOut>(IRequest<TOut> request, RequestDispatchContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Validate(request, context, ct);
        var registration = registry.Handler(request.GetType());
        var policies = registry.Policies(registration.RequestType);
        using var activity = PortiaTelemetry.StartExecute(policies.Name);
        var started = PortiaTelemetry.StartTimestamp();
        var transport = context.Invocation.TransportName;
        PortiaTelemetry.RequestStarted(policies.Name, transport);
        var outcome = "fault";
        var completed = false;
        try
        {
            var authorization = await AuthorizeAsync(registration, policies, request, context, ct).ConfigureAwait(false);
            var result = authorization.IsSuccess
                ? await InvokeAsync(registration, policies, request, context, ct).ConfigureAwait(false)
                : Result<TOut>.Failure(authorization.Error);
            PortiaTelemetry.RecordOutcome(activity, result.IsSuccess, result.Error);
            outcome = PortiaTelemetry.Outcome(result.IsSuccess, result.Error);
            completed = true;
            return result;
        }
        catch (OperationCanceledException) { _ = activity?.SetStatus(ActivityStatusCode.Error); throw; }
        catch (Exception ex) { _ = activity?.AddException(ex); _ = activity?.SetStatus(ActivityStatusCode.Error); throw; }
        finally { PortiaTelemetry.RequestFinished(started, policies.Name, transport, Finish(completed, outcome, ct)); }
    }

    // The three dispatch paths deliberately repeat this preamble rather than share it through a
    // generic unifier: the streaming path cannot use one (a yield return cannot sit inside a try
    // with a catch), so a unifier would have covered two of three call sites while costing a
    // five-argument delegate at each. Only the outcome-selection rule is shared.
    static string Finish(bool completed, string outcome, CancellationToken ct) =>
        completed ? outcome : ct.IsCancellationRequested ? "canceled" : "fault";

    /// <inheritdoc />
    public async IAsyncEnumerable<TOut> DispatchStreamAsync<TOut>(IStreamRequest<TOut> request, RequestDispatchContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Validate(request, context, ct);
        var registration = registry.Handler(request.GetType());
        var policies = registry.Policies(registration.RequestType);
        using var activity = PortiaTelemetry.StartExecute(policies.Name);
        var started = PortiaTelemetry.StartTimestamp();
        var transport = context.Invocation.TransportName;
        PortiaTelemetry.RequestStarted(policies.Name, transport);
        var outcome = "fault";
        var completed = false;
        try
        {
            var authorization = await AuthorizeAsync(registration, policies, request, context, ct).ConfigureAwait(false);
            if (!authorization.IsSuccess)
            {
                PortiaTelemetry.RecordOutcome(activity, false, authorization.Error);
                outcome = PortiaTelemetry.Outcome(false, authorization.Error);
                // A denial is a settled outcome, not an incomplete enumeration: mark it settled
                // before throwing so the finally reports the denial rather than "fault".
                completed = true;
                throw new RequestAuthorizationException(authorization.Error);
            }
            await foreach (var item in CatchStream(EnumerateStream(registration, policies, request, context, ct), activity, ct).ConfigureAwait(false))
                yield return item;
            outcome = "success";
            completed = true;
        }
        finally { PortiaTelemetry.RequestFinished(started, policies.Name, transport, Finish(completed, outcome, ct)); }
    }

    // A yield return cannot sit inside a try with a catch, so enumeration is wrapped in methods
    // that can, keeping mid-stream faults on the activity like the unary paths.
    static async IAsyncEnumerable<TOut> CatchStream<TOut>(IAsyncEnumerable<TOut> source, Activity? activity,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var enumerator = source.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                var (hasValue, value) = await MoveNextAsync(enumerator, activity).ConfigureAwait(false);
                if (!hasValue)
                    yield break;
                yield return value!;
            }
        }
        finally { await DisposeAsync(enumerator, activity).ConfigureAwait(false); }
    }

    static async ValueTask<(bool HasValue, TOut? Value)> MoveNextAsync<TOut>(IAsyncEnumerator<TOut> enumerator, Activity? activity)
    {
        try
        {
            return await enumerator.MoveNextAsync().ConfigureAwait(false) ? (true, enumerator.Current) : (false, default);
        }
        catch (OperationCanceledException) { _ = activity?.SetStatus(ActivityStatusCode.Error); throw; }
        catch (Exception ex) { _ = activity?.AddException(ex); _ = activity?.SetStatus(ActivityStatusCode.Error); throw; }
    }

    static async ValueTask DisposeAsync<TOut>(IAsyncEnumerator<TOut> enumerator, Activity? activity)
    {
        try { await enumerator.DisposeAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) { _ = activity?.SetStatus(ActivityStatusCode.Error); throw; }
        catch (Exception ex) { _ = activity?.AddException(ex); _ = activity?.SetStatus(ActivityStatusCode.Error); throw; }
    }

    // Behaviors are selected by scope assignability, which says nothing about request shape: a
    // request can implement a no-result family interface and IRequest<TOut> at once, so a
    // behavior registered against that family reaches this dispatch without being able to serve
    // it. Skipping a shape it cannot serve is the same answer scope matching already gives.
    ValueTask<Result> InvokeAsync(RequestHandlerRegistration registration, RequestPolicies policies, IRequest request,
        RequestDispatchContext context, CancellationToken ct)
    {
        RequestPipelineNext next = async token =>
        {
            var result = await ((IRequestInvocation)registration).InvokeAsync(services, request, context, token).ConfigureAwait(false);
            ValidateResult(result, registration.HandlerType, "request handler");
            return result;
        };
        foreach (var behavior in policies.BehaviorsInnermostFirst)
        {
            if (behavior is not IRequestBehaviorInvocation current)
                continue;
            var owner = behavior.BehaviorType;
            var remainder = next;
            next = async token =>
            {
                var result = await current.InvokeAsync(services, request, context, remainder, token).ConfigureAwait(false);
                ValidateResult(result, owner, "pipeline behavior");
                return result;
            };
        }
        return next(ct);
    }

    ValueTask<Result<TOut>> InvokeAsync<TOut>(RequestHandlerRegistration registration, RequestPolicies policies, IRequest<TOut> request,
        RequestDispatchContext context, CancellationToken ct)
    {
        RequestPipelineNext<TOut> next = async token =>
        {
            var result = await ((IRequestInvocation<TOut>)registration).InvokeAsync(services, request, context, token).ConfigureAwait(false);
            ValidateResult(result, registration.HandlerType, "request handler");
            return result;
        };
        foreach (var behavior in policies.BehaviorsInnermostFirst)
        {
            if (behavior is not IRequestBehaviorInvocation<TOut> current)
                continue;
            var owner = behavior.BehaviorType;
            var remainder = next;
            next = async token =>
            {
                var result = await current.InvokeAsync(services, request, context, remainder, token).ConfigureAwait(false);
                ValidateResult(result, owner, "pipeline behavior");
                return result;
            };
        }
        return next(ct);
    }

    IAsyncEnumerable<TOut> EnumerateStream<TOut>(RequestHandlerRegistration registration, RequestPolicies policies,
        IStreamRequest<TOut> request, RequestDispatchContext context, CancellationToken ct)
    {
        StreamRequestPipelineNext<TOut> next = token => ((IStreamRequestInvocation<TOut>)registration).Invoke(services, request, context, token);
        foreach (var behavior in policies.BehaviorsInnermostFirst)
        {
            if (behavior is not IStreamRequestBehaviorInvocation<TOut> current)
                continue;
            var remainder = next;
            next = token => current.Invoke(services, request, context, remainder, token);
        }
        return next(ct);
    }

    static void ValidateResult(Result result, Type ownerType, string role)
    {
        try { _ = result.IsSuccess; }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Portia {role} '{ownerType.FullName}' returned an uninitialized Result.", ex);
        }
    }

    static void ValidateResult<TOut>(Result<TOut> result, Type ownerType, string role)
    {
        try { _ = result.IsSuccess; }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"Portia {role} '{ownerType.FullName}' returned an uninitialized Result<{typeof(TOut).FullName}>.", ex);
        }
    }

    async ValueTask<Result> AuthorizeAsync(RequestHandlerRegistration registration, RequestPolicies policies,
        IRequestBase request, RequestDispatchContext context, CancellationToken ct)
    {
        var principal = await RunAuthorizersAsync(policies.PrincipalAuthorizers, request, context, ct).ConfigureAwait(false);
        if (!principal.IsSuccess)
            return principal;
        if (registration.Permission is not null)
        {
            var started = PortiaTelemetry.StartTimestamp();
            var permission = await services.GetRequiredService<IPermissionEvaluator>()
                .EvaluateAsync(context.Actor, registration.Permission(request), ct).ConfigureAwait(false);
            PortiaTelemetry.AuthorizationFinished(started, "permission", "permission", PortiaTelemetry.Outcome(permission.IsSuccess, permission.Error));
            if (!permission.IsSuccess)
                return permission;
        }
        return await RunAuthorizersAsync(policies.ResourceAuthorizers, request, context, ct).ConfigureAwait(false);
    }

    async ValueTask<Result> RunAuthorizersAsync(RequestAuthorizerRegistration[] authorizers,
        IRequestBase request, RequestDispatchContext context, CancellationToken ct)
    {
        foreach (var authorizer in authorizers)
        {
            var started = PortiaTelemetry.StartTimestamp();
            var result = await authorizer.AuthorizeAsync(services, request, context, ct).ConfigureAwait(false);
            ValidateResult(result, authorizer.AuthorizerType, "request authorizer");
            PortiaTelemetry.AuthorizationFinished(started, authorizer.ComponentName, authorizer.StageName,
                PortiaTelemetry.Outcome(result.IsSuccess, result.Error));
            if (!result.IsSuccess)
                return result;
        }
        return Result.Success;
    }

    // The context's constructor already rejected a null actor, so there is nothing left to check
    // about it here.
    static void Validate(IRequestBase request, RequestDispatchContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
    }
}
