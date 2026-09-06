using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class ExecutionLifecycleTests
{
    [Fact]
    public async Task StreamingAuthorizationAndHandlerShareExecutionWithoutSharingMutablePrincipals()
    {
        var services = new ServiceCollection();
        var handler = new Handler();
        var authorizer = new Authorizer();
        var clock = new Clock();
        _ = services.AddContracts();
        _ = services.AddSingleton(handler);
        _ = services.AddSingleton(authorizer);
        _ = services.AddSingleton<TimeProvider>(clock);
        _ = services.AddSingleton<RequestHandlerRegistration>(new StreamRequestRegistration<Query, Handler, int>());
        _ = services.AddSingleton<RequestAuthorizerRegistration>(new RequestAuthorizerRegistration<Query, Authorizer>());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var parent = new RequestContext<Query>(new Query(), RequestActor.CreateSystem("parent"));
        var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
        var values = new List<int>();
        await foreach (var value in bus.StreamAsync(new Query(), parent))
            values.Add(value);
        Assert.Equal([1, 2], values);
        var context = Assert.IsAssignableFrom<IRequestContext<Query>>(handler.Context);
        Assert.Equal(authorizer.Context!.ExecutionId, context.ExecutionId);
        Assert.Equal(authorizer.Context.StartedAt, context.StartedAt);
        Assert.Equal(clock.GetUtcNow(), context.StartedAt);
        Assert.Equal(parent.CorrelationId, context.CorrelationId);
        Assert.Equal(parent.CauseId, context.CausationId);
        Assert.NotEqual(parent.ExecutionId, context.ExecutionId);
        Assert.False(context.Actor.HasClaim("mutated", "yes"));
        Assert.False(parent.Actor.HasClaim("mutated", "yes"));
        _ = Assert.IsType<DirectInvocation>(context.Invocation);
    }

    sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    }
    sealed record Query : IStreamRequest<int>;
    sealed class Handler : IStreamRequestHandler<Query, int>
    {
        public IRequestContext<Query>? Context { get; private set; }
        public async IAsyncEnumerable<int> HandleAsync(IRequestContext<Query> context, [EnumeratorCancellation] CancellationToken ct)
        {
            Context = context;
            yield return 1;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            Assert.Equal(Context.ExecutionId, context.ExecutionId);
            yield return 2;
        }
    }
    sealed class Authorizer : IRequestAuthorizer<Query>
    {
        public IRequestContext<Query>? Context { get; private set; }
        public ValueTask<Result> AuthorizeAsync(IRequestContext<Query> context, ClaimsPrincipal actor, CancellationToken ct = default)
        {
            Context = context;
            ((ClaimsIdentity)context.Actor.Identity!).AddClaim(new Claim("mutated", "yes"));
            ((ClaimsIdentity)actor.Identity!).AddClaim(new Claim("mutated", "yes"));
            return ValueTask.FromResult(Result.Success);
        }
    }
}
