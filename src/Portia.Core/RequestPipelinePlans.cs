using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

static class PipelineContinuationContract
{
    internal const string SingleUseMessage =
        "A request pipeline continuation may be invoked at most once and only during its behavior invocation.";
}

sealed class RequestPipelineFrame<TRequest>
{
    const long Completed = long.MinValue;
    static readonly ConditionalWeakTable<RequestPipelineFrame<TRequest>, int[]> ExtendedStates = [];
    long _state;

    internal RequestPipelineFrame(object plan, IServiceProvider services, RequestHandlerRegistration registration,
        TRequest request, IRequestContext context, int behaviorCount)
    {
        Plan = plan;
        Services = services;
        Registration = registration;
        Request = request;
        Context = context;
        if (behaviorCount > 31)
        {
            ExtendedStates.Add(this, new int[behaviorCount - 31]);
        }
    }

    internal object Plan { get; }
    internal IServiceProvider Services { get; }
    internal RequestHandlerRegistration Registration { get; }
    internal TRequest Request { get; }
    internal IRequestContext Context { get; }

    internal void Use(int position)
    {
        if (position >= 31)
        {
            var extendedState = ExtendedStates.GetValue(this, static _ =>
                throw new InvalidOperationException("Missing extended request-pipeline state."));
            if ((Volatile.Read(ref _state) & Completed) != 0 ||
                Interlocked.CompareExchange(ref extendedState[position - 31], 1, 0) != 0 ||
                (Volatile.Read(ref _state) & Completed) != 0)
            {
                throw new InvalidOperationException(PipelineContinuationContract.SingleUseMessage);
            }

            return;
        }

        var used = 1L << (position * 2);
        var occupied = used | (used << 1);
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if ((state & (Completed | occupied)) != 0)
                throw new InvalidOperationException(PipelineContinuationContract.SingleUseMessage);
            if (Interlocked.CompareExchange(ref _state, state | used, state) == state)
                return;
        }
    }

    internal void Close(int position)
    {
        if (position >= 31)
        {
            var extendedState = ExtendedStates.GetValue(this, static _ =>
                throw new InvalidOperationException("Missing extended request-pipeline state."));
            _ = Interlocked.Exchange(ref extendedState[position - 31], 2);
            return;
        }

        _ = Interlocked.Or(ref _state, 2L << (position * 2));
    }

    internal void Complete() => _ = Interlocked.Or(ref _state, Completed);
}

sealed class UnaryRequestPipelinePlan
{
    static readonly AsyncLocal<RequestPipelineFrame<IRequest>?> Current = new();
    readonly (IRequestBehaviorInvocation Invocation, Type Owner)[] _behaviors;
    readonly RequestPipelineNext[] _continuations;

    internal UnaryRequestPipelinePlan(IEnumerable<RequestPipelineBehaviorRegistration> registrations)
    {
        _behaviors = registrations.Reverse()
            .Where(registration => registration is IRequestBehaviorInvocation)
            .Select(registration => ((IRequestBehaviorInvocation)registration, registration.BehaviorType)).ToArray();
        _continuations = Enumerable.Range(0, _behaviors.Length).Select(CreateContinuation).ToArray();
    }

    internal bool IsEmpty => _behaviors.Length == 0;

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
            return ValidateAsync(((IRequestInvocation)frame.Registration)
                .InvokeAsync(frame.Services, frame.Request, frame.Context, ct), frame.Registration.HandlerType,
                "request handler");
        }

        return InvokeBehaviorAsync(frame, position, ct);
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

sealed class ResultRequestPipelinePlan<TOut>
{
    static readonly AsyncLocal<RequestPipelineFrame<IRequest<TOut>>?> Current = new();
    readonly (IRequestBehaviorInvocation<TOut> Invocation, Type Owner)[] _behaviors;
    readonly RequestPipelineNext<TOut>[] _continuations;

    internal ResultRequestPipelinePlan(IEnumerable<RequestPipelineBehaviorRegistration> registrations)
    {
        _behaviors = registrations.Reverse()
            .Where(registration => registration is IRequestBehaviorInvocation<TOut>)
            .Select(registration => ((IRequestBehaviorInvocation<TOut>)registration, registration.BehaviorType))
            .ToArray();
        _continuations = Enumerable.Range(0, _behaviors.Length).Select(CreateContinuation).ToArray();
    }

    internal bool IsEmpty => _behaviors.Length == 0;

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

    RequestPipelineNext<TOut> CreateContinuation(int position) => token => Continue(position, token);

    ValueTask<Result<TOut>> Continue(int position, CancellationToken ct)
    {
        var frame = Current.Value;
        if (frame is null || !ReferenceEquals(frame.Plan, this))
            throw new InvalidOperationException(PipelineContinuationContract.SingleUseMessage);
        frame.Use(position);
        return InvokeAtAsync(frame, position + 1, ct);
    }

    ValueTask<Result<TOut>> InvokeAtAsync(RequestPipelineFrame<IRequest<TOut>> frame, int position,
        CancellationToken ct)
    {
        if (position == _behaviors.Length)
        {
            return ValidateAsync(((IRequestInvocation<TOut>)frame.Registration)
                .InvokeAsync(frame.Services, frame.Request, frame.Context, ct), frame.Registration.HandlerType,
                "request handler");
        }

        return InvokeBehaviorAsync(frame, position, ct);
    }

    ValueTask<Result<TOut>> InvokeBehaviorAsync(RequestPipelineFrame<IRequest<TOut>> frame, int position,
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

sealed class StreamRequestPipelinePlan<TOut>
{
    static readonly AsyncLocal<RequestPipelineFrame<IStreamRequest<TOut>>?> Current = new();
    readonly IStreamRequestBehaviorInvocation<TOut>[] _behaviors;
    readonly StreamRequestPipelineNext<TOut>[] _continuations;

    internal StreamRequestPipelinePlan(IEnumerable<RequestPipelineBehaviorRegistration> registrations)
    {
        _behaviors = registrations.OfType<IStreamRequestBehaviorInvocation<TOut>>().Reverse().ToArray();
        _continuations = Enumerable.Range(0, _behaviors.Length).Select(CreateContinuation).ToArray();
    }

    internal IAsyncEnumerable<TOut> Invoke(IServiceProvider services, RequestHandlerRegistration registration,
        IStreamRequest<TOut> request, IRequestContext context, CancellationToken ct) =>
        new FrameBoundEnumerable(this,
            new RequestPipelineFrame<IStreamRequest<TOut>>(this, services, registration, request, context,
                _behaviors.Length), ct);

    StreamRequestPipelineNext<TOut> CreateContinuation(int position) => token => Continue(position, token);

    IAsyncEnumerable<TOut> Continue(int position, CancellationToken ct)
    {
        var frame = Current.Value;
        if (frame is null || !ReferenceEquals(frame.Plan, this))
            throw new InvalidOperationException(PipelineContinuationContract.SingleUseMessage);
        frame.Use(position);
        return InvokeAt(frame, position + 1, ct);
    }

    IAsyncEnumerable<TOut> InvokeAt(RequestPipelineFrame<IStreamRequest<TOut>> frame, int position,
        CancellationToken ct) =>
        position == _behaviors.Length
            ? ((IStreamRequestInvocation<TOut>)frame.Registration)
            .Invoke(frame.Services, frame.Request, frame.Context, ct)
            : EnumerateBehavior(frame, position, ct);

    async IAsyncEnumerable<TOut> EnumerateBehavior(RequestPipelineFrame<IStreamRequest<TOut>> frame, int position,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await foreach (var item in _behaviors[position]
                               .Invoke(frame.Services, frame.Request, frame.Context, _continuations[position], ct)
                               .WithCancellation(ct).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            frame.Close(position);
        }
    }

    async IAsyncEnumerable<TOut> Enumerate(RequestPipelineFrame<IStreamRequest<TOut>> frame,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await foreach (var item in InvokeAt(frame, 0, ct).WithCancellation(ct)
                               .ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            frame.Complete();
        }
    }

    sealed class FrameBoundEnumerable(StreamRequestPipelinePlan<TOut> plan,
        RequestPipelineFrame<IStreamRequest<TOut>> frame, CancellationToken dispatchToken) : IAsyncEnumerable<TOut>
    {
        public IAsyncEnumerator<TOut> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new FrameBoundEnumerator(frame,
                plan.Enumerate(frame, dispatchToken).GetAsyncEnumerator(cancellationToken));
    }

    sealed class FrameBoundEnumerator(RequestPipelineFrame<IStreamRequest<TOut>> frame,
        IAsyncEnumerator<TOut> inner) : IAsyncEnumerator<TOut>
    {
        public TOut Current => inner.Current;

        public ValueTask<bool> MoveNextAsync() => InvokeWithFrame(inner.MoveNextAsync);

        public ValueTask DisposeAsync() => InvokeWithFrame(inner.DisposeAsync);

        TValue InvokeWithFrame<TValue>(Func<TValue> action)
        {
            var prior = StreamRequestPipelinePlan<TOut>.Current.Value;
            StreamRequestPipelinePlan<TOut>.Current.Value = frame;
            try
            {
                return action();
            }
            finally
            {
                StreamRequestPipelinePlan<TOut>.Current.Value = prior;
            }
        }
    }
}
