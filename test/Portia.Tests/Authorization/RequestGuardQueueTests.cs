using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Verifies that queue retries resolve and execute guards in fresh delivery scopes.</summary>
public sealed class RequestGuardQueueTests
{
    /// <summary>A transient guard failure abandons, then a fresh scoped guard runs before redelivery succeeds.</summary>
    [Fact]
    public async Task ShouldResolveAndRunGuardAgainInFreshRedeliveryScope()
    {
        var state = new AttemptState();
        var services = new ServiceCollection();
        _ = services.AddSingleton(state);
        _ = services.AddScoped<ScopeMarker>();
        _ = services.AddScoped<IRequestActorValidator, ActorValidator>();
        _ = services.AddScoped<IQueuedRequestTerminalHandler, TerminalHandler>();
        _ = services.AddPortia()
            .AddRequestHandler<RetryCommandHandler>()
            .AddRequestGuard<RetryCommandGuard>();
        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var first = new Queued(new RetryCommand(), 1);
        var second = new Queued(new RetryCommand(), 2);
        var scopeFactory = new DependencyInjectionQueueDeliveryScopeFactory(
            provider.GetRequiredService<IServiceScopeFactory>());

        await new QueueRunner(new Queue([first, second]), scopeFactory).RunAsync();

        Assert.True(first.Abandoned);
        Assert.False(first.Completed);
        Assert.True(second.Completed);
        Assert.False(second.Abandoned);
        Assert.Equal(2, state.Guards.Count);
        Assert.NotEqual(state.Guards[0].GuardId, state.Guards[1].GuardId);
        Assert.Equal(["guard-failure", "guard-success", "handler"], state.Calls);
        Assert.Equal(state.Guards[1].ScopeId, Assert.Single(state.Handlers).Id);
    }

    internal sealed record RetryCommand : IRequest;

    internal sealed class RetryCommandGuard(AttemptState state, ScopeMarker scope) : IRequestGuard<RetryCommand>
    {
        readonly Guid _id = Guid.NewGuid();

        public ValueTask<Result> GuardAsync(IRequestContext<RetryCommand> context, CancellationToken ct)
        {
            state.Guards.Add((_id, scope.Id));
            if (state.Guards.Count == 1)
            {
                state.Calls.Add("guard-failure");
                return ValueTask.FromResult(Result.Failure(new RequestError(RequestErrorKind.Conflict,
                    "The read model is not ready yet.", true)));
            }

            state.Calls.Add("guard-success");
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class RetryCommandHandler(AttemptState state, ScopeMarker scope) : IRequestHandler<RetryCommand>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<RetryCommand> context, CancellationToken ct)
        {
            state.Calls.Add("handler");
            state.Handlers.Add(scope);
            return ValueTask.FromResult(Result.Success);
        }
    }

    internal sealed class AttemptState
    {
        public List<string> Calls { get; } = [];
        public List<(Guid GuardId, int ScopeId)> Guards { get; } = [];
        public List<ScopeMarker> Handlers { get; } = [];
    }

    internal sealed class ScopeMarker
    {
        static int Next;

        public int Id { get; } = Interlocked.Increment(ref Next);
    }

    sealed class ActorValidator : IRequestActorValidator
    {
        public ValueTask<Result<ClaimsPrincipal>> ValidateAsync(string? token, CancellationToken ct = default) =>
            ValueTask.FromResult(Result<ClaimsPrincipal>.Success(RequestActor.System));
    }

    sealed class TerminalHandler : IQueuedRequestTerminalHandler
    {
        public ValueTask HandleAsync(QueuedRequestFailureContext context, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }

    sealed class Queue(IReadOnlyList<IQueuedRequest> items) : IRequestQueueConsumer
    {
        public async IAsyncEnumerable<IQueuedRequest> ReadAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return item;
            }
        }
    }

    sealed class Queued(IRequest request, uint attempt) : IQueuedRequest
    {
        public bool Completed { get; private set; }
        public bool Abandoned { get; private set; }
        public IRequest Request { get; } = request;
        public RequestMetadata Metadata { get; } = RequestMetadata.Create();
        public RequestInvocation Invocation => new QueueInvocation("queue://test/guards/redelivery", Attempt);
        public uint Attempt { get; } = attempt;
        public bool SupportsDurableAttempts => true;
        public string? ActorToken => null;

        public ValueTask CompleteAsync(CancellationToken ct = default)
        {
            Completed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask AbandonAsync(CancellationToken ct = default)
        {
            Abandoned = true;
            return ValueTask.CompletedTask;
        }
    }
}
