using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

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
