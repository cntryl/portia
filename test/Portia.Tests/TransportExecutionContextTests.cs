using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Exercises request context at transport boundaries.</summary>
public sealed class TransportExecutionContextTests
{
    /// <summary>Verifies the public transport context contract.</summary>
    [Fact]
    public async Task HttpHandlerReceivesConcreteIngressWithoutQuerySecrets()
    {
        var builder = WebApplication.CreateBuilder();
        _ = builder.WebHost.UseTestServer();
        var bus = new RecordingBus();
        _ = builder.Services.AddSingleton<IRequestBus>(bus);
        _ = builder.Services.AddPortia();
        await using var app = builder.Build();
        _ = app.MapPortiaPost<TransportCommand>("/context/{amount}");
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync("/context/3?secret=hidden", new { });
        _ = response.EnsureSuccessStatusCode();
        var context = Assert.Single(bus.Contexts);
        var http = Assert.IsType<HttpInvocation>(context.Invocation);
        Assert.Equal("POST", http.Method);
        Assert.Equal("/context/3", http.Path);
        Assert.Equal("/context/{amount}", http.RoutePattern);
        Assert.False(string.IsNullOrWhiteSpace(http.RequestIdentifier));
        Assert.Equal(4, context.ExecutionId.Version);
        await app.StopAsync();
    }

    /// <summary>Verifies the public transport context contract.</summary>
    [Fact]
    public async Task QueueRedeliveryKeepsRequestIdentityButStartsNewExecution()
    {
        var metadata = RequestMetadata.Create();
        var first = new Queued(metadata, 1);
        var second = new Queued(metadata, 2);
        var bus = new RecordingBus();
        await new QueueRunner(new Consumer(first, second), RequestDeliveryScopes.FixedQueue(bus, new Validator()))
            .RunAsync();
        Assert.Equal(2, bus.Contexts.Count);
        Assert.All(bus.Contexts, ctx => Assert.Equal(metadata.RequestId, ctx.RequestId));
        Assert.NotEqual(bus.Contexts[0].ExecutionId, bus.Contexts[1].ExecutionId);
        Assert.Equal(new QueueInvocation("queue://test/work/actual", 2), bus.Contexts[1].Invocation);
        Assert.All(bus.Contexts, ctx => Assert.True(RequestActor.IsSystem(ctx.Actor)));
    }

    /// <summary>Verifies the public transport context contract.</summary>
    public sealed record TransportCommand(int Amount) : IRequest, ICallable;

    sealed class RecordingBus : IRequestBus
    {
        public List<RequestDispatchContext> Contexts { get; } = [];

        public RequestDispatchContext CreateContext(ClaimsPrincipal actor, RequestMetadata? metadata = null) =>
            new(actor, metadata: metadata);

        public ValueTask<Result> DispatchAsync(IRequest request, RequestDispatchContext context,
            CancellationToken ct = default)
        {
            Contexts.Add(context);
            return ValueTask.FromResult(Result.Success);
        }

        public ValueTask<Result<T>> DispatchAsync<T>(IRequest<T> request, RequestDispatchContext context,
            CancellationToken ct = default) => throw new NotSupportedException();

        public IAsyncEnumerable<T> DispatchStreamAsync<T>(IStreamRequest<T> request, RequestDispatchContext context,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    sealed class Validator : IRequestActorValidator
    {
        public ValueTask<Result<ClaimsPrincipal>> ValidateAsync(string? token, CancellationToken ct = default)
            => ValueTask.FromResult(Result<ClaimsPrincipal>.Success(RequestActor.System));
    }

    sealed class Queued(RequestMetadata metadata, uint attempt) : IQueuedRequest
    {
        public IRequest Request => new TransportCommand(3);
        public string? ActorToken => null;
        public uint Attempt => attempt;
        public RequestMetadata Metadata => metadata;
        public RequestInvocation Invocation => new QueueInvocation("queue://test/work/actual", attempt);
        public ValueTask CompleteAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask AbandonAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    sealed class Consumer(params IQueuedRequest[] requests) : IRequestQueueConsumer
    {
        public async IAsyncEnumerable<IQueuedRequest> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            foreach (var request in requests)
                yield return request;
        }
    }
}
