using System.Security.Claims;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>Measures the in-process request dispatch overhead around an already-completed handler.</summary>
[MemoryDiagnoser]
public partial class RequestDispatchBenchmarks
{
    DispatchState _fiveBehaviors = null!;
    DispatchState _oneBehavior = null!;
    DispatchState _withoutBehaviors = null!;

    /// <summary>Builds the three stable application compositions used by the benchmarks.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _withoutBehaviors = Create(builder => builder.AddRequestHandler<BenchmarkRequestHandler>());
        _oneBehavior = Create(builder => builder.AddRequestHandler<BenchmarkRequestHandler>()
            .AddRequestPipelineBehavior<FirstBehavior>());
        _fiveBehaviors = Create(builder => builder.AddRequestHandler<BenchmarkRequestHandler>()
            .AddRequestPipelineBehavior<FirstBehavior>()
            .AddRequestPipelineBehavior<SecondBehavior>()
            .AddRequestPipelineBehavior<ThirdBehavior>()
            .AddRequestPipelineBehavior<FourthBehavior>()
            .AddRequestPipelineBehavior<FifthBehavior>());
    }

    /// <summary>Disposes the application service providers.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _withoutBehaviors.Dispose();
        _oneBehavior.Dispose();
        _fiveBehaviors.Dispose();
    }

    /// <summary>Measures dispatch without authorization or pipeline behaviors.</summary>
    [Benchmark(Baseline = true)]
    public ValueTask<Result> NoBehaviors() => _withoutBehaviors.Dispatch();

    /// <summary>Measures dispatch through one behavior.</summary>
    [Benchmark]
    public ValueTask<Result> OneBehavior() => _oneBehavior.Dispatch();

    /// <summary>Measures dispatch through five behaviors.</summary>
    [Benchmark]
    public ValueTask<Result> FiveBehaviors() => _fiveBehaviors.Dispatch();

    static DispatchState Create(Action<PortiaBuilder> configure)
    {
        var services = new ServiceCollection();
        var builder = services.AddPortia();
        configure(builder);
        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
        return new DispatchState(provider, scope, bus,
            bus.CreateContext(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    sealed class DispatchState(ServiceProvider provider, IServiceScope scope, IRequestBus bus,
        RequestDispatchContext context) : IDisposable
    {
        static readonly BenchmarkRequest Request = new();

        public ValueTask<Result> Dispatch() => bus.DispatchAsync(Request, context);

        public void Dispose()
        {
            scope.Dispose();
            provider.Dispose();
        }
    }

    internal sealed record BenchmarkRequest : IRequest;

    internal sealed class BenchmarkRequestHandler : IRequestHandler<BenchmarkRequest>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<BenchmarkRequest> context, CancellationToken ct) =>
            ValueTask.FromResult(Result.Success);
    }

    internal abstract class PassThroughBehavior : IRequestPipelineBehavior<BenchmarkRequest>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<BenchmarkRequest> context,
            RequestPipelineNext continuation, CancellationToken ct) => continuation(ct);
    }

    internal sealed class FirstBehavior : PassThroughBehavior;
    internal sealed class SecondBehavior : PassThroughBehavior;
    internal sealed class ThirdBehavior : PassThroughBehavior;
    internal sealed class FourthBehavior : PassThroughBehavior;
    internal sealed class FifthBehavior : PassThroughBehavior;
}

[PortiaJsonContext]
[JsonSerializable(typeof(RequestDispatchBenchmarks.BenchmarkRequest))]
[JsonSerializable(typeof(ProcessorBenchmarkEvent))]
[JsonSerializable(typeof(SerializationBenchmarkEvent))]
sealed partial class BenchmarkJsonContext : JsonSerializerContext;
