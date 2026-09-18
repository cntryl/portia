using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

sealed class StreamRequestPipelinePlan<TOut>
{
    static readonly AsyncLocal<RequestPipelineFrame<IStreamRequest<TOut>>?> Current = new();
    readonly IStreamRequestBehaviorInvocation<TOut>[] _behaviors;
    readonly StreamRequestPipelineNext<TOut>[] _continuations;
    readonly RequestGuardRegistration[] _guards;

    internal StreamRequestPipelinePlan(IEnumerable<RequestPipelineBehaviorRegistration> registrations,
        RequestGuardRegistration[] guards)
    {
        _guards = guards;
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
            ? _guards.Length == 0
                ? ((IStreamRequestInvocation<TOut>)frame.Registration)
                .Invoke(frame.Services, frame.Request, frame.Context, ct)
                : EnumerateGuardedHandler(frame, ct)
            : EnumerateBehavior(frame, position, ct);

    // Guards sit innermost, exactly as for unary requests: behaviors wrap them, and a failure ends
    // the stream before the handler runs or any item is produced.
    async IAsyncEnumerable<TOut> EnumerateGuardedHandler(RequestPipelineFrame<IStreamRequest<TOut>> frame,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var guarded = await RequestGuardRunner.RunAsync(frame.Services, _guards, frame.Request, frame.Context, ct)
            .ConfigureAwait(false);
        if (!guarded.IsSuccess)
            throw new RequestGuardException(guarded.Error);
        await foreach (var item in ((IStreamRequestInvocation<TOut>)frame.Registration)
                       .Invoke(frame.Services, frame.Request, frame.Context, ct).WithCancellation(ct)
                       .ConfigureAwait(false))
            yield return item;
    }

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

    sealed class FrameBoundEnumerable(
        StreamRequestPipelinePlan<TOut> plan,
        RequestPipelineFrame<IStreamRequest<TOut>> frame,
        CancellationToken dispatchToken) : IAsyncEnumerable<TOut>
    {
        public IAsyncEnumerator<TOut> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new FrameBoundEnumerator(frame,
                plan.Enumerate(frame, dispatchToken).GetAsyncEnumerator(cancellationToken));
    }

    sealed class FrameBoundEnumerator(
        RequestPipelineFrame<IStreamRequest<TOut>> frame,
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
