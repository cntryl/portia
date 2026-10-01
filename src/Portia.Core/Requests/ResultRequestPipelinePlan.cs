namespace Cntryl.Portia;

sealed class ResultRequestPipelinePlan<TOut>
{
    static readonly AsyncLocal<RequestPipelineFrame<IRequest<TOut>>?> Current = new();
    readonly (IRequestBehaviorInvocation<TOut> Invocation, Type Owner)[] _behaviors;
    readonly RequestGuardRegistration[] _guards;

    internal ResultRequestPipelinePlan(IEnumerable<RequestPipelineBehaviorRegistration> registrations,
        RequestGuardRegistration[] guards)
    {
        _behaviors = registrations.Reverse()
            .Where(registration => registration is IRequestBehaviorInvocation<TOut>)
            .Select(registration => ((IRequestBehaviorInvocation<TOut>)registration, registration.BehaviorType))
            .ToArray();
        _guards = guards;
    }

    internal bool IsEmpty => _behaviors.Length == 0 && _guards.Length == 0;

    internal ValueTask<Result<TOut>> InvokeAsync(IServiceProvider services,
        RequestHandlerRegistration registration, IRequest<TOut> request, IRequestContext context,
        CancellationToken ct)
    {
        var frame = new RequestPipelineFrame<IRequest<TOut>>(this, services, registration, request, context,
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

    static async ValueTask<Result<TOut>> AwaitInvocationAsync(ValueTask<Result<TOut>> pending,
        RequestPipelineFrame<IRequest<TOut>> frame, RequestPipelineFrame<IRequest<TOut>>? prior)
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

    RequestPipelineNext<TOut> CreateContinuation(RequestPipelineFrame<IRequest<TOut>> frame, int position) =>
        token => Continue(frame, position, token);

    ValueTask<Result<TOut>> Continue(RequestPipelineFrame<IRequest<TOut>> frame, int position, CancellationToken ct)
    {
        if (!ReferenceEquals(Current.Value, frame))
            throw new InvalidOperationException(PipelineContinuationContract.SingleUseMessage);
        frame.Use(position);
        return InvokeAtAsync(frame, position + 1, ct);
    }

    ValueTask<Result<TOut>> InvokeAtAsync(RequestPipelineFrame<IRequest<TOut>> frame, int position,
        CancellationToken ct)
    {
        if (position == _behaviors.Length)
        {
            return _guards.Length == 0
                ? ValidateAsync(((IRequestInvocation<TOut>)frame.Registration)
                    .InvokeAsync(frame.Services, frame.Request, frame.Context, ct), frame.Registration.HandlerType,
                    "request handler")
                : InvokeGuardedHandlerAsync(frame, ct);
        }

        return InvokeBehaviorAsync(frame, position, ct);
    }

    async ValueTask<Result<TOut>> InvokeGuardedHandlerAsync(RequestPipelineFrame<IRequest<TOut>> frame,
        CancellationToken ct)
    {
        var guarded = await RequestGuardRunner.RunAsync(frame.Services, _guards, frame.Request, frame.Context, ct)
            .ConfigureAwait(false);
        if (!guarded.IsSuccess)
            return Result<TOut>.Failure(guarded.Error);
        return await ValidateAsync(((IRequestInvocation<TOut>)frame.Registration)
            .InvokeAsync(frame.Services, frame.Request, frame.Context, ct), frame.Registration.HandlerType,
            "request handler").ConfigureAwait(false);
    }

    ValueTask<Result<TOut>> InvokeBehaviorAsync(RequestPipelineFrame<IRequest<TOut>> frame, int position,
        CancellationToken ct)
    {
        var behavior = _behaviors[position];
        try
        {
            var pending = behavior.Invocation.InvokeAsync(frame.Services, frame.Request, frame.Context,
                CreateContinuation(frame, position), ct);
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

    static async ValueTask<Result<TOut>> AwaitBehaviorAsync(ValueTask<Result<TOut>> pending,
        RequestPipelineFrame<IRequest<TOut>> frame, int position, Type owner)
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

    static ValueTask<Result<TOut>> ValidateAsync(ValueTask<Result<TOut>> pending, Type owner, string role)
    {
        if (!pending.IsCompletedSuccessfully)
            return AwaitValidationAsync(pending, owner, role);
        var result = pending.Result;
        Validate(result, owner, role);
        return ValueTask.FromResult(result);
    }

    static async ValueTask<Result<TOut>> AwaitValidationAsync(ValueTask<Result<TOut>> pending, Type owner,
        string role)
    {
        var result = await pending.ConfigureAwait(false);
        Validate(result, owner, role);
        return result;
    }

    static void Validate(Result<TOut> result, Type owner, string role)
    {
        try
        {
            _ = result.IsSuccess;
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"Portia {role} '{owner.FullName}' returned an uninitialized Result<{typeof(TOut).FullName}>.", ex);
        }
    }
}
