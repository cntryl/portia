namespace Cntryl.Portia;

sealed class UnaryRequestPipelinePlan
{
    static readonly AsyncLocal<RequestPipelineFrame<IRequest>?> Current = new();
    readonly (IRequestBehaviorInvocation Invocation, Type Owner)[] _behaviors;
    readonly RequestPipelineNext[] _continuations;
    readonly RequestGuardRegistration[] _guards;

    internal UnaryRequestPipelinePlan(IEnumerable<RequestPipelineBehaviorRegistration> registrations,
        RequestGuardRegistration[] guards)
    {
        _behaviors = registrations.Reverse()
            .Where(registration => registration is IRequestBehaviorInvocation)
            .Select(registration => ((IRequestBehaviorInvocation)registration, registration.BehaviorType)).ToArray();
        _continuations = Enumerable.Range(0, _behaviors.Length).Select(CreateContinuation).ToArray();
        _guards = guards;
    }

    internal bool IsEmpty => _behaviors.Length == 0 && _guards.Length == 0;

    internal ValueTask<Result> InvokeAsync(IServiceProvider services, RequestHandlerRegistration registration,
        IRequest request, IRequestContext context, CancellationToken ct)
    {
        var frame = new RequestPipelineFrame<IRequest>(this, services, registration, request, context,
            _behaviors.Length);
        var prior = Current.Value;
        Current.Value = frame;
        try
        {
            var pending = InvokeAtAsync(frame, 0, ct);
            if (pending.IsCompletedSuccessfully)
            {
                var result = pending.Result;
                frame.Complete();
                return ValueTask.FromResult(result);
            }

            return AwaitInvocationAsync(pending, frame, prior);
        }
        catch
        {
            frame.Complete();
            throw;
        }
        finally
        {
            Current.Value = prior;
        }
    }

    static async ValueTask<Result> AwaitInvocationAsync(ValueTask<Result> pending,
        RequestPipelineFrame<IRequest> frame, RequestPipelineFrame<IRequest>? prior)
    {
        try
        {
            return await pending.ConfigureAwait(false);
        }
        finally
        {
            frame.Complete();
            Current.Value = prior;
        }
    }

    RequestPipelineNext CreateContinuation(int position) => token => Continue(position, token);

    ValueTask<Result> Continue(int position, CancellationToken ct)
    {
        var frame = Current.Value;
        if (frame is null || !ReferenceEquals(frame.Plan, this))
            throw new InvalidOperationException(PipelineContinuationContract.SingleUseMessage);
        frame.Use(position);
        return InvokeAtAsync(frame, position + 1, ct);
    }

    ValueTask<Result> InvokeAtAsync(RequestPipelineFrame<IRequest> frame, int position, CancellationToken ct)
    {
        if (position == _behaviors.Length)
        {
            return _guards.Length == 0
                ? ValidateAsync(((IRequestInvocation)frame.Registration)
                    .InvokeAsync(frame.Services, frame.Request, frame.Context, ct), frame.Registration.HandlerType,
                    "request handler")
                : InvokeGuardedHandlerAsync(frame, ct);
        }

        return InvokeBehaviorAsync(frame, position, ct);
    }

    async ValueTask<Result> InvokeGuardedHandlerAsync(RequestPipelineFrame<IRequest> frame, CancellationToken ct)
    {
        var guarded = await RequestGuardRunner.RunAsync(frame.Services, _guards, frame.Request, frame.Context, ct)
            .ConfigureAwait(false);
        if (!guarded.IsSuccess)
            return guarded;
        return await ValidateAsync(((IRequestInvocation)frame.Registration)
            .InvokeAsync(frame.Services, frame.Request, frame.Context, ct), frame.Registration.HandlerType,
            "request handler").ConfigureAwait(false);
    }

    ValueTask<Result> InvokeBehaviorAsync(RequestPipelineFrame<IRequest> frame, int position,
        CancellationToken ct)
    {
        var behavior = _behaviors[position];
        try
        {
            var pending = behavior.Invocation.InvokeAsync(frame.Services, frame.Request, frame.Context,
                _continuations[position], ct);
            if (pending.IsCompletedSuccessfully)
            {
                var result = pending.Result;
                Validate(result, behavior.Owner, "pipeline behavior");
                frame.Close(position);
                return ValueTask.FromResult(result);
            }

            return AwaitBehaviorAsync(pending, frame, position, behavior.Owner);
        }
        catch
        {
            frame.Close(position);
            throw;
        }
    }

    static async ValueTask<Result> AwaitBehaviorAsync(ValueTask<Result> pending,
        RequestPipelineFrame<IRequest> frame, int position, Type owner)
    {
        try
        {
            var result = await pending.ConfigureAwait(false);
            Validate(result, owner, "pipeline behavior");
            return result;
        }
        finally
        {
            frame.Close(position);
        }
    }

    static ValueTask<Result> ValidateAsync(ValueTask<Result> pending, Type owner, string role)
    {
        if (!pending.IsCompletedSuccessfully)
            return AwaitValidationAsync(pending, owner, role);
        var result = pending.Result;
        Validate(result, owner, role);
        return ValueTask.FromResult(result);
    }

    static async ValueTask<Result> AwaitValidationAsync(ValueTask<Result> pending, Type owner, string role)
    {
        var result = await pending.ConfigureAwait(false);
        Validate(result, owner, role);
        return result;
    }

    static void Validate(Result result, Type owner, string role)
    {
        try
        {
            _ = result.IsSuccess;
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Portia {role} '{owner.FullName}' returned an uninitialized Result.", ex);
        }
    }
}
