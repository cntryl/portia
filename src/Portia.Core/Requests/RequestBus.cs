using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
    public ValueTask<Result> AuthorizeAsync(IRequestBase request, RequestDispatchContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Validate(request, context, ct);
        var registration = registry.Handler(request.GetType());
        var policies = registry.Policies(registration.RequestType);
        if (policies.IsUnprotected)
            ThrowUnprotectedRequest(registration.RequestType);
        return policies.HasAuthorizers || registration.Permission is not null
            ? AuthorizeAsync(registration, policies, request, registration.CreateContext(request, context), ct)
            : ValueTask.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public async ValueTask<Result> DispatchAsync(IRequest request, RequestDispatchContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Validate(request, context, ct);
        var registration = registry.Handler(request.GetType());
        var policies = registry.Policies(registration.RequestType);
        var transport = context.Invocation.TransportName;
        using var activity = PortiaTelemetry.StartExecute(policies.Name, transport);
        var started = PortiaTelemetry.StartTimestamp();
        PortiaTelemetry.RequestStarted(policies.Name, transport);
        var outcome = "fault";
        var completed = false;
        try
        {
            var result = await ExecuteAsync(registration, policies, request, context, ct).ConfigureAwait(false);
            PortiaTelemetry.RecordOutcome(activity, result.IsSuccess, result.Error);
            outcome = PortiaTelemetry.Outcome(result.IsSuccess, result.Error);
            completed = true;
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            PortiaTelemetry.RecordCanceled(activity);
            throw;
        }
        catch (EventStreamConcurrencyException)
        {
            var error = ConcurrencyError();
            PortiaTelemetry.RecordOutcome(activity, false, error);
            outcome = PortiaTelemetry.Outcome(false, error);
            completed = true;
            throw;
        }
        catch (Exception ex)
        {
            PortiaTelemetry.RecordFault(activity, ex);
            throw;
        }
        finally
        {
            PortiaTelemetry.RequestFinished(started, policies.Name, transport, Finish(completed, outcome, ct));
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<TOut>> DispatchAsync<TOut>(IRequest<TOut> request, RequestDispatchContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Validate(request, context, ct);
        var registration = registry.Handler(request.GetType());
        var policies = registry.Policies(registration.RequestType);
        var transport = context.Invocation.TransportName;
        using var activity = PortiaTelemetry.StartExecute(policies.Name, transport);
        var started = PortiaTelemetry.StartTimestamp();
        PortiaTelemetry.RequestStarted(policies.Name, transport);
        var outcome = "fault";
        var completed = false;
        try
        {
            var result = await ExecuteAsync(registration, policies, request, context, ct).ConfigureAwait(false);
            PortiaTelemetry.RecordOutcome(activity, result.IsSuccess, result.Error);
            outcome = PortiaTelemetry.Outcome(result.IsSuccess, result.Error);
            completed = true;
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            PortiaTelemetry.RecordCanceled(activity);
            throw;
        }
        catch (EventStreamConcurrencyException)
        {
            var error = ConcurrencyError();
            PortiaTelemetry.RecordOutcome(activity, false, error);
            outcome = PortiaTelemetry.Outcome(false, error);
            completed = true;
            throw;
        }
        catch (Exception ex)
        {
            PortiaTelemetry.RecordFault(activity, ex);
            throw;
        }
        finally
        {
            PortiaTelemetry.RequestFinished(started, policies.Name, transport, Finish(completed, outcome, ct));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TOut> DispatchStreamAsync<TOut>(IStreamRequest<TOut> request,
        RequestDispatchContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Validate(request, context, ct);
        var registration = registry.Handler(request.GetType());
        var policies = registry.Policies(registration.RequestType);
        var requestContext = registration.CreateContext(request, context);
        var transport = context.Invocation.TransportName;
        using var activity = PortiaTelemetry.StartExecute(policies.Name, transport);
        var started = PortiaTelemetry.StartTimestamp();
        PortiaTelemetry.RequestStarted(policies.Name, transport);
        var outcome = "fault";
        var completed = false;
        var streamFailure = new StrongBox<RequestError?>();
        try
        {
            if (policies.IsUnprotected)
                ThrowUnprotectedRequest(registration.RequestType);
            var authorization =
                await AuthorizeAsync(registration, policies, request, requestContext, ct).ConfigureAwait(false);
            if (!authorization.IsSuccess)
            {
                PortiaTelemetry.RecordOutcome(activity, false, authorization.Error);
                outcome = PortiaTelemetry.Outcome(false, authorization.Error);
                // A denial is a settled outcome, not an incomplete enumeration: mark it settled
                // before throwing so the finally reports the denial rather than "fault".
                completed = true;
                throw new RequestAuthorizationException(authorization.Error);
            }

            await foreach (var item in CatchStream(EnumerateStream(registration, policies, request, requestContext, ct),
                               activity, streamFailure, policies.HasGuards, ct).ConfigureAwait(false))
            {
                yield return item;
            }

            outcome = "success";
            PortiaTelemetry.RecordOutcome(activity, true, null);
            completed = true;
        }
        finally
        {
            // A failed guard or commit-time concurrency race is a settled request outcome, not a fault.
            if (!completed && streamFailure.Value is { } streamError)
            {
                outcome = PortiaTelemetry.Outcome(false, streamError);
                completed = true;
            }

            if (!completed)
            {
                if (ct.IsCancellationRequested)
                {
                    PortiaTelemetry.RecordCanceled(activity);
                }
                else if (activity is not null && activity.GetTagItem("portia.outcome") is null)
                {
                    PortiaTelemetry.RecordFault(activity);
                }
            }

            PortiaTelemetry.RequestFinished(started, policies.Name, transport, Finish(completed, outcome, ct));
        }
    }

    // The three dispatch paths deliberately repeat this preamble rather than share it through a
    // generic unifier: the streaming path cannot use one (a yield return cannot sit inside a try
    // with a catch), so a unifier would have covered two of three call sites while costing a
    // five-argument delegate at each. Only the outcome-selection rule is shared.
    static string Finish(bool completed, string outcome, CancellationToken ct) =>
        completed ? outcome : ct.IsCancellationRequested ? "canceled" : "fault";

    static RequestError ConcurrencyError() => new(RequestErrorKind.Conflict,
        "The request conflicted with a concurrent update.", true);

    // A yield return cannot sit inside a try with a catch, so enumeration is wrapped in methods
    // that can, keeping mid-stream faults on the activity like the unary paths.
    static async IAsyncEnumerable<TOut> CatchStream<TOut>(IAsyncEnumerable<TOut> source, Activity? activity,
        StrongBox<RequestError?> streamFailure, bool hasGuards, [EnumeratorCancellation] CancellationToken ct)
    {
        var enumerator = source.GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                var (hasValue, value) = await MoveNextAsync(enumerator, activity, streamFailure, hasGuards, ct)
                    .ConfigureAwait(false);
                if (!hasValue)
                {
                    yield break;
                }
                else
                {
                    yield return value!;
                }
            }
        }
        finally
        {
            await DisposeAsync(enumerator, activity, streamFailure, ct).ConfigureAwait(false);
        }
    }

    static async ValueTask<(bool HasValue, TOut? Value)> MoveNextAsync<TOut>(IAsyncEnumerator<TOut> enumerator,
        Activity? activity, StrongBox<RequestError?> streamFailure, bool hasGuards, CancellationToken ct)
    {
        try
        {
            return await enumerator.MoveNextAsync().ConfigureAwait(false)
                ? (true, enumerator.Current)
                : (false, default);
        }
        catch (RequestGuardException ex) when (hasGuards)
        {
            streamFailure.Value = ex.Error;
            PortiaTelemetry.RecordOutcome(activity, false, ex.Error);
            throw;
        }
        catch (EventStreamConcurrencyException)
        {
            var error = ConcurrencyError();
            streamFailure.Value = error;
            PortiaTelemetry.RecordOutcome(activity, false, error);
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            PortiaTelemetry.RecordCanceled(activity);
            throw;
        }
        catch (Exception ex)
        {
            PortiaTelemetry.RecordFault(activity, ex);
            throw;
        }
    }

    static async ValueTask DisposeAsync<TOut>(IAsyncEnumerator<TOut> enumerator, Activity? activity,
        StrongBox<RequestError?> streamFailure, CancellationToken ct)
    {
        try
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            streamFailure.Value = null;
            PortiaTelemetry.RecordCanceled(activity);
            throw;
        }
        catch (EventStreamConcurrencyException)
        {
            var error = ConcurrencyError();
            streamFailure.Value = error;
            PortiaTelemetry.RecordOutcome(activity, false, error);
            throw;
        }
        catch (Exception ex)
        {
            streamFailure.Value = null;
            PortiaTelemetry.RecordFault(activity, ex);
            throw;
        }
    }

    // Behaviors are selected by scope assignability, which says nothing about request shape: a
    // request can implement a no-result family interface and IRequest<TOut> at once, so a
    // behavior registered against that family reaches this dispatch without being able to serve
    // it. Skipping a shape it cannot serve is the same answer scope matching already gives.
    ValueTask<Result> ExecuteAsync(RequestHandlerRegistration registration, RequestPolicies policies,
        IRequest request, RequestDispatchContext dispatchContext, CancellationToken ct)
    {
        if (policies.IsUnprotected)
            ThrowUnprotectedRequest(registration.RequestType);
        if (!policies.HasAuthorizers && registration.Permission is null && policies.Unary.IsEmpty)
            return InvokeHandlerAsync(registration, request, dispatchContext, ct);
        if (!policies.HasAuthorizers && registration.Permission is null)
            return InvokeAsync(registration, policies, request, registration.CreateContext(request, dispatchContext),
                ct);
        return AuthorizeAndInvokeAsync(registration, policies, request, dispatchContext, ct);
    }

    async ValueTask<Result> AuthorizeAndInvokeAsync(RequestHandlerRegistration registration,
        RequestPolicies policies, IRequest request, RequestDispatchContext dispatchContext, CancellationToken ct)
    {
        var requestContext = registration.CreateContext(request, dispatchContext);
        var authorization = await AuthorizeAsync(registration, policies, request, requestContext, ct)
            .ConfigureAwait(false);
        return authorization.IsSuccess
            ? await InvokeAsync(registration, policies, request, requestContext, ct).ConfigureAwait(false)
            : authorization;
    }

    ValueTask<Result<TOut>> ExecuteAsync<TOut>(RequestHandlerRegistration registration, RequestPolicies policies,
        IRequest<TOut> request, RequestDispatchContext dispatchContext, CancellationToken ct)
    {
        if (policies.IsUnprotected)
            ThrowUnprotectedRequest(registration.RequestType);
        if (!policies.HasAuthorizers && registration.Permission is null && policies.Result<TOut>().IsEmpty)
            return InvokeHandlerAsync(registration, request, dispatchContext, ct);
        if (!policies.HasAuthorizers && registration.Permission is null)
        {
            return InvokeAsync(registration, policies, request, registration.CreateContext(request, dispatchContext),
                ct);
        }

        return AuthorizeAndInvokeAsync(registration, policies, request, dispatchContext, ct);
    }

    async ValueTask<Result<TOut>> AuthorizeAndInvokeAsync<TOut>(RequestHandlerRegistration registration,
        RequestPolicies policies, IRequest<TOut> request, RequestDispatchContext dispatchContext,
        CancellationToken ct)
    {
        var requestContext = registration.CreateContext(request, dispatchContext);
        var authorization = await AuthorizeAsync(registration, policies, request, requestContext, ct)
            .ConfigureAwait(false);
        return authorization.IsSuccess
            ? await InvokeAsync(registration, policies, request, requestContext, ct).ConfigureAwait(false)
            : Result<TOut>.Failure(authorization.Error);
    }

    ValueTask<Result> InvokeAsync(RequestHandlerRegistration registration, RequestPolicies policies, IRequest request,
        IRequestContext context, CancellationToken ct)
    {
        if (policies.Unary.IsEmpty)
            return InvokeHandlerAsync(registration, request, context, ct);
        return policies.Unary.InvokeAsync(services, registration, request, context, ct);
    }

    async ValueTask<Result> InvokeHandlerAsync(RequestHandlerRegistration registration, IRequest request,
        IRequestContext context, CancellationToken ct)
    {
        var result = await ((IRequestInvocation)registration).InvokeAsync(services, request, context, ct)
            .ConfigureAwait(false);
        ValidateResult(result, registration.HandlerType, "request handler");
        return result;
    }

    async ValueTask<Result> InvokeHandlerAsync(RequestHandlerRegistration registration, IRequest request,
        RequestDispatchContext context, CancellationToken ct)
    {
        var result = await ((IRequestInvocation)registration).InvokeUnsharedAsync(services, request, context, ct)
            .ConfigureAwait(false);
        ValidateResult(result, registration.HandlerType, "request handler");
        return result;
    }

    ValueTask<Result<TOut>> InvokeAsync<TOut>(RequestHandlerRegistration registration, RequestPolicies policies,
        IRequest<TOut> request,
        IRequestContext context, CancellationToken ct)
    {
        var plan = policies.Result<TOut>();
        if (plan.IsEmpty)
            return InvokeHandlerAsync(registration, request, context, ct);
        return plan.InvokeAsync(services, registration, request, context, ct);
    }

    async ValueTask<Result<TOut>> InvokeHandlerAsync<TOut>(RequestHandlerRegistration registration,
        IRequest<TOut> request, IRequestContext context, CancellationToken ct)
    {
        var result = await ((IRequestInvocation<TOut>)registration).InvokeAsync(services, request, context, ct)
            .ConfigureAwait(false);
        ValidateResult(result, registration.HandlerType, "request handler");
        return result;
    }

    async ValueTask<Result<TOut>> InvokeHandlerAsync<TOut>(RequestHandlerRegistration registration,
        IRequest<TOut> request, RequestDispatchContext context, CancellationToken ct)
    {
        var result = await ((IRequestInvocation<TOut>)registration)
            .InvokeUnsharedAsync(services, request, context, ct).ConfigureAwait(false);
        ValidateResult(result, registration.HandlerType, "request handler");
        return result;
    }

    IAsyncEnumerable<TOut> EnumerateStream<TOut>(RequestHandlerRegistration registration, RequestPolicies policies,
        IStreamRequest<TOut> request, IRequestContext context, CancellationToken ct) =>
        policies.Stream<TOut>().Invoke(services, registration, request, context, ct);

    static void ValidateResult(Result result, Type ownerType, string role)
    {
        try
        {
            _ = result.IsSuccess;
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Portia {role} '{ownerType.FullName}' returned an uninitialized Result.", ex);
        }
    }

    static void ValidateResult<TOut>(Result<TOut> result, Type ownerType, string role)
    {
        try
        {
            _ = result.IsSuccess;
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Portia {role} '{ownerType.FullName}' returned an uninitialized Result<{typeof(TOut).FullName}>.", ex);
        }
    }

    async ValueTask<Result> AuthorizeAsync(RequestHandlerRegistration registration, RequestPolicies policies,
        IRequestBase request, IRequestContext context, CancellationToken ct)
    {
        var principal = await RunAuthorizersAsync(policies.PrincipalAuthorizers, request, context, ct)
            .ConfigureAwait(false);
        if (!principal.IsSuccess)
        {
            return principal;
        }

        if (registration.Permission is not null)
        {
            var started = PortiaTelemetry.StartTimestamp();
            var outcome = "fault";
            Result permission;
            try
            {
                permission = await services.GetRequiredService<IPermissionEvaluator>()
                    .EvaluateAsync(context.Actor, registration.Permission(request), ct).ConfigureAwait(false);
                outcome = PortiaTelemetry.Outcome(permission.IsSuccess, permission.Error);
            }
            catch (Exception exception)
            {
                outcome = PortiaTelemetry.ExceptionOutcome(exception, ct);
                throw;
            }
            finally
            {
                PortiaTelemetry.AuthorizationFinished(started, "permission", "permission", outcome);
            }

            if (!permission.IsSuccess)
            {
                return permission;
            }
        }

        return await RunAuthorizersAsync(policies.ResourceAuthorizers, request, context, ct).ConfigureAwait(false);
    }

    async ValueTask<Result> RunAuthorizersAsync(RequestAuthorizerRegistration[] authorizers,
        IRequestBase request, IRequestContext context, CancellationToken ct)
    {
        foreach (var authorizer in authorizers)
        {
            var started = PortiaTelemetry.StartTimestamp();
            var outcome = "fault";
            Result result;
            try
            {
                result = await authorizer.AuthorizeAsync(services, request, context, ct).ConfigureAwait(false);
                ValidateResult(result, authorizer.AuthorizerType, "request authorizer");
                outcome = PortiaTelemetry.Outcome(result.IsSuccess, result.Error);
            }
            catch (Exception exception)
            {
                outcome = PortiaTelemetry.ExceptionOutcome(exception, ct);
                throw;
            }
            finally
            {
                PortiaTelemetry.AuthorizationFinished(started, authorizer.ComponentName, authorizer.StageName,
                    outcome);
            }

            if (!result.IsSuccess)
            {
                return result;
            }
        }

        return Result.Success;
    }

    // An unprotected request under RequireAuthorization() is a composition mistake, not an access
    // decision: throwing names the fix, where a Forbidden result would pass for a real denial.
    [DoesNotReturn]
    static void ThrowUnprotectedRequest(Type requestType) =>
        throw new InvalidOperationException(
            $"Request type '{requestType.FullName}' has no applicable request authorizer or [RequiresPermission], and RequireAuthorization() is enabled. Register an authorizer, declare a permission, or call RequireAuthorization(options => options.AllowAnonymous<{requestType.Name}>()).");

    // The context's constructor already rejected a null actor, so there is nothing left to check
    // about it here.
    static void Validate(IRequestBase request, RequestDispatchContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
    }
}
