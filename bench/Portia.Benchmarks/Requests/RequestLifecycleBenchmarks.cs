using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cntryl.Portia;

/// <summary>Measures ordinary request lifecycles separately from reused-context dispatch.</summary>
[MemoryDiagnoser]
public class RequestLifecycleBenchmarks : IDisposable
{
    RuntimeApplication _application = null!;

    /// <summary>Gets or sets the boundary included in each invocation.</summary>
    [Params("dispatch", "send", "scope")]
    public string Boundary { get; set; } = "send";

    /// <summary>Composes generated handlers and verifies result, principal and cleanup controls.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _application = new RuntimeApplication();
        _application.QualifyAsync().GetAwaiter().GetResult();
    }

    /// <summary>Measures a completed command at the selected boundary.</summary>
    [Benchmark]
    public ValueTask<Result> Command() => _application.CommandAsync(Boundary);

    /// <summary>Measures a result-bearing query at the selected boundary.</summary>
    [Benchmark]
    public ValueTask<Result<int>> Query() => _application.QueryAsync(Boundary);

    /// <summary>Measures complete enumeration and disposal of eight stream items.</summary>
    [Benchmark]
    public ValueTask<int> FiniteStream() => _application.StreamAsync(Boundary);

    /// <summary>Releases the reused request scope and application.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        _application?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Measures genuinely yielding handlers through the ordinary API.</summary>
[MemoryDiagnoser]
public class YieldingRequestLifecycleBenchmarks : IDisposable
{
    RuntimeApplication _application = null!;

    /// <summary>Composes yielding handlers and a yielding authorization/behavior/guard path.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _application = new RuntimeApplication(yield: true);
        _application.QualifyAsync().GetAwaiter().GetResult();
    }

    /// <summary>Measures one yielding command.</summary>
    [Benchmark]
    public ValueTask<Result> Command() => _application.CommandAsync("send");

    /// <summary>Measures one yielding query.</summary>
    [Benchmark]
    public ValueTask<Result<int>> Query() => _application.QueryAsync("send");

    /// <summary>Measures eight asynchronously yielded stream items.</summary>
    [Benchmark]
    public ValueTask<int> FiniteStream() => _application.StreamAsync("send");

    /// <summary>Measures permission, row authorization, behavior, guard and handler.</summary>
    [Benchmark]
    public ValueTask<Result> AuthorizationBehaviorGuard() => _application.SecureAsync();

    /// <summary>Releases the application.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        _application?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Separates context construction, actor copies and synchronous preflight paths.</summary>
[MemoryDiagnoser]
public class RequestContextAndPreflightBenchmarks : IDisposable
{
    RuntimeApplication _application = null!;

    /// <summary>Creates an explicit authenticated principal with two claims.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _application = new RuntimeApplication();
        _application.QualifyAsync().GetAwaiter().GetResult();
    }

    /// <summary>Measures creation of IDs, time and the principal snapshot.</summary>
    [Benchmark]
    public RequestDispatchContext ContextCreation() => _application.Bus.CreateContext(RuntimeApplication.Actor);

    /// <summary>Measures the independent principal returned by the public Actor property.</summary>
    [Benchmark]
    public ClaimsPrincipal ActorRead() => _application.Context.Actor;

    /// <summary>Measures completed permission, authorizer, behavior and read-only guard paths.</summary>
    [Benchmark]
    public ValueTask<Result> AuthorizationBehaviorGuard() => _application.SecureAsync();

    /// <summary>Releases the application.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        _application?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Measures bounded telemetry cohorts through ordinary request handling.</summary>
[MemoryDiagnoser]
public class RequestLifecycleTelemetryBenchmarks : IDisposable
{
    RuntimeApplication _application = null!;
    ActivityListener? _traces;
    MeterListener? _metrics;
    Activity? _parent;

    /// <summary>Gets or sets one isolated instrumentation cohort.</summary>
    [Params("none", "propagation", "sampled", "metrics", "logs-disabled", "logs-enabled")]
    public string Cohort { get; set; } = "none";

    /// <summary>Enables only the declared listener or structured outcome logger.</summary>
    [GlobalSetup]
    public void Setup()
    {
        if (Cohort == "propagation")
            _parent = new Activity("benchmark-propagation-parent").SetIdFormat(ActivityIdFormat.W3C).Start();
        if (Cohort is "propagation" or "sampled")
        {
            var sampling = Cohort == "sampled" ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.PropagationData;
            _traces = new ActivityListener
            {
                ShouldListenTo = source => source.Name == PortiaTelemetry.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => sampling,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => sampling
            };
            ActivitySource.AddActivityListener(_traces);
        }
        if (Cohort == "metrics")
        {
            _metrics = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == PortiaTelemetry.SourceName)
                        listener.EnableMeasurementEvents(instrument);
                }
            };
            _metrics.SetMeasurementEventCallback<double>((_, _, _, _) => { });
            _metrics.SetMeasurementEventCallback<long>((_, _, _, _) => { });
            _metrics.Start();
        }
        _application = new RuntimeApplication(outcomeLogging: Cohort.StartsWith("logs-", StringComparison.Ordinal),
            loggingEnabled: Cohort == "logs-enabled");
        _application.QualifyAsync().GetAwaiter().GetResult();
    }

    /// <summary>Measures one normal request with application-owned instrumentation.</summary>
    [Benchmark]
    public ValueTask<Result> Command() => _application.CommandAsync("send");

    /// <summary>Disposes all listeners without waiting on an exporter.</summary>
    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <inheritdoc />
    public void Dispose()
    {
        _application?.Dispose();
        _traces?.Dispose();
        _metrics?.Dispose();
        _parent?.Dispose();
        GC.SuppressFinalize(this);
    }
}

sealed class RuntimeApplication : IDisposable
{
    internal static readonly ClaimsPrincipal Actor = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "benchmark-actor"), new Claim("permission", "benchmark.execute")], "benchmark"));
    static readonly RuntimeCommand Command = new();
    static readonly RuntimeQuery Query = new();
    static readonly RuntimeStream Stream = new();
    static readonly RuntimeSecure Secure = new();
    readonly ServiceProvider _provider;
    readonly IServiceScope _scope;
    readonly RuntimeState _state;

    internal RuntimeApplication(bool yield = false, bool outcomeLogging = false, bool loggingEnabled = false)
    {
        var services = new ServiceCollection();
        _state = new RuntimeState { Yield = yield };
        services.AddSingleton(_state).AddScoped<RuntimeScopeMarker>();
        services.AddSingleton<IPermissionEvaluator, RuntimePermissions>();
        services.AddSingleton<ILogger<RuntimeOutcomeBehavior>>(new RuntimeLogger(loggingEnabled));
        var portia = services.AddPortia().AddRequestHandler<RuntimeCommandHandler>()
            .AddRequestHandler<RuntimeQueryHandler>().AddRequestHandler<RuntimeStreamHandler>()
            .AddRequestHandler<RuntimeSecureHandler>().AddRequestAuthorizer<RuntimeAuthorizer>()
            .AddRequestPipelineBehavior<RuntimeSecureBehavior>().AddRequestGuard<RuntimeGuard>();
        if (outcomeLogging)
            portia.AddRequestPipelineBehavior<RuntimeOutcomeBehavior>();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _scope = _provider.CreateScope();
        Bus = _scope.ServiceProvider.GetRequiredService<IRequestBus>();
        Context = Bus.CreateContext(Actor);
    }

    internal IRequestBus Bus { get; }
    internal RequestDispatchContext Context { get; }

    internal async ValueTask<Result> CommandAsync(string boundary)
    {
        if (boundary == "scope")
        {
            await using var scope = _provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IRequestBus>().SendAsync(Command, Actor);
        }
        return boundary == "dispatch" ? await Bus.DispatchAsync(Command, Context) : await Bus.SendAsync(Command, Actor);
    }

    internal async ValueTask<Result<int>> QueryAsync(string boundary)
    {
        if (boundary == "scope")
        {
            await using var scope = _provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IRequestBus>().SendAsync(Query, Actor);
        }
        return boundary == "dispatch" ? await Bus.DispatchAsync(Query, Context) : await Bus.SendAsync(Query, Actor);
    }

    internal async ValueTask<int> StreamAsync(string boundary)
    {
        if (boundary == "scope")
        {
            await using var scope = _provider.CreateAsyncScope();
            return await CollectAsync(scope.ServiceProvider.GetRequiredService<IRequestBus>().StreamAsync(Stream, Actor));
        }
        return await CollectAsync(boundary == "dispatch" ? Bus.DispatchStreamAsync(Stream, Context) : Bus.StreamAsync(Stream, Actor));
    }

    internal ValueTask<Result> SecureAsync() => Bus.SendAsync(Secure, Actor);

    static async ValueTask<int> CollectAsync(IAsyncEnumerable<int> values)
    {
        var sum = 0;
        await foreach (var value in values)
            sum += value;
        return sum;
    }

    internal async Task QualifyAsync()
    {
        foreach (var boundary in new[] { "dispatch", "send", "scope" })
        {
            if (!(await CommandAsync(boundary)).IsSuccess || (await QueryAsync(boundary)).Value != 42 || await StreamAsync(boundary) != 28)
                throw new InvalidOperationException("Lifecycle fixture failed to consume the expected results.");
            if (boundary == "scope")
            {
                await using var scope = _provider.CreateAsyncScope();
                await VerifySequenceAsync(scope.ServiceProvider.GetRequiredService<IRequestBus>().StreamAsync(Stream, Actor));
            }
            else
            {
                await VerifySequenceAsync(boundary == "dispatch" ? Bus.DispatchStreamAsync(Stream, Context) : Bus.StreamAsync(Stream, Actor));
            }
        }
        var disposed = _state.StreamDisposals;
        await foreach (var _ in Bus.StreamAsync(Stream, Actor))
            break;
        if (_state.StreamDisposals != disposed + 1)
            throw new InvalidOperationException("Early stream consumption did not dispose the enumerator.");
        var before = _state.ScopeDisposals;
        await CommandAsync("scope");
        await CommandAsync("scope");
        if (_state.ScopeDisposals != before + 2)
            throw new InvalidOperationException("Fresh request scopes were not disposed.");
        var snapshot = Context.Actor;
        ((ClaimsIdentity)snapshot.Identity!).AddClaim(new Claim("mutated", "true"));
        if (Actor.HasClaim("mutated", "true") || Context.Actor.HasClaim("mutated", "true"))
            throw new InvalidOperationException("Principal copies were not independent.");
        _state.RecordOrder = true;
        _state.Order.Clear();
        if (!(await SecureAsync()).IsSuccess || string.Join(',', _state.Order) != "permission,authorizer,behavior,guard,handler")
            throw new InvalidOperationException("Preflight fixture violated pipeline ordering.");
        _state.RecordOrder = false;
    }

    static async Task VerifySequenceAsync(IAsyncEnumerable<int> values)
    {
        var index = 0;
        await foreach (var value in values)
        {
            if (index >= 8 || value != index++)
                throw new InvalidOperationException("Finite lifecycle stream did not preserve the expected sequence.");
        }
        if (index != 8)
            throw new InvalidOperationException("Finite lifecycle stream emitted the wrong number of items.");
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }
}

sealed class RuntimeState
{
    internal bool Yield { get; init; }
    internal bool RecordOrder { get; set; }
    internal List<string> Order { get; } = [];
    internal int ScopeDisposals;
    internal int StreamDisposals;
    internal void Record(string stage) { if (RecordOrder) Order.Add(stage); }
}

sealed class RuntimeScopeMarker(RuntimeState state) : IDisposable
{
    internal int Value { get; } = 42;
    public void Dispose() => Interlocked.Increment(ref state.ScopeDisposals);
}

sealed record RuntimeCommand : IRequest;
sealed record RuntimeQuery : IRequest<int>;
sealed record RuntimeStream : IStreamRequest<int>;
[RequiresPermission("benchmark.execute")]
sealed record RuntimeSecure : IRequest;

sealed class RuntimeCommandHandler(RuntimeState state, RuntimeScopeMarker marker) : IRequestHandler<RuntimeCommand>
{
    public async ValueTask<Result> HandleAsync(IRequestContext<RuntimeCommand> context, CancellationToken ct)
    {
        if (state.Yield)
            await Task.Yield();
        return marker.Value == 42 ? Result.Success : throw new InvalidOperationException();
    }
}

sealed class RuntimeQueryHandler(RuntimeState state, RuntimeScopeMarker marker) : IRequestHandler<RuntimeQuery, int>
{
    public async ValueTask<Result<int>> HandleAsync(IRequestContext<RuntimeQuery> context, CancellationToken ct)
    {
        if (state.Yield)
            await Task.Yield();
        return Result<int>.Success(marker.Value);
    }
}

sealed class RuntimeStreamHandler(RuntimeState state, RuntimeScopeMarker marker) : IStreamRequestHandler<RuntimeStream, int>
{
    public async IAsyncEnumerable<int> HandleAsync(IRequestContext<RuntimeStream> context, [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            for (var value = 0; value < 8; value++)
            {
                ct.ThrowIfCancellationRequested();
                if (state.Yield)
                    await Task.Yield();
                yield return marker.Value == 42 ? value : throw new InvalidOperationException();
            }
        }
        finally { Interlocked.Increment(ref state.StreamDisposals); }
    }
}

sealed class RuntimePermissions(RuntimeState state) : IPermissionEvaluator
{
    public async ValueTask<Result> EvaluateAsync(ClaimsPrincipal actor, string permission, CancellationToken ct = default)
    {
        state.Record("permission");
        if (state.Yield)
            await Task.Yield();
        return actor.HasClaim("permission", permission) ? Result.Success : Result.Failure(new(RequestErrorKind.Forbidden, "expected"));
    }
}

sealed class RuntimeAuthorizer(RuntimeState state) : IRequestAuthorizer<RuntimeSecure>
{
    public async ValueTask<Result> AuthorizeAsync(IRequestContext<RuntimeSecure> context, CancellationToken ct)
    {
        state.Record("authorizer");
        if (state.Yield)
            await Task.Yield();
        return Result.Success;
    }
}

sealed class RuntimeGuard(RuntimeState state) : IRequestGuard<RuntimeSecure>
{
    public async ValueTask<Result> GuardAsync(IRequestContext<RuntimeSecure> context, CancellationToken ct)
    {
        state.Record("guard");
        if (state.Yield)
            await Task.Yield();
        return Result.Success;
    }
}

sealed class RuntimeSecureBehavior(RuntimeState state) : IRequestPipelineBehavior<RuntimeSecure>
{
    public async ValueTask<Result> HandleAsync(IRequestContext<RuntimeSecure> context, RequestPipelineNext continuation, CancellationToken ct)
    {
        state.Record("behavior");
        if (state.Yield)
            await Task.Yield();
        return await continuation(ct);
    }
}

sealed class RuntimeSecureHandler(RuntimeState state) : IRequestHandler<RuntimeSecure>
{
    public async ValueTask<Result> HandleAsync(IRequestContext<RuntimeSecure> context, CancellationToken ct)
    {
        state.Record("handler");
        if (state.Yield)
            await Task.Yield();
        return Result.Success;
    }
}

sealed partial class RuntimeOutcomeBehavior(ILogger<RuntimeOutcomeBehavior> logger) : IRequestPipelineBehavior<RuntimeCommand>
{
    public async ValueTask<Result> HandleAsync(IRequestContext<RuntimeCommand> context, RequestPipelineNext continuation, CancellationToken ct)
    {
        var result = await continuation(ct);
        LogOutcome(logger, result.IsSuccess);
        return result;
    }
    [LoggerMessage(EventId = 7100, Level = LogLevel.Information, Message = "Benchmark command completed: {Success}")]
    static partial void LogOutcome(ILogger logger, bool success);
}

sealed class RuntimeLogger(bool enabled) : ILogger<RuntimeOutcomeBehavior>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => enabled;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => GC.KeepAlive(state);
}

[PortiaJsonContext]
[JsonSerializable(typeof(RuntimeCommand))]
[JsonSerializable(typeof(RuntimeQuery))]
[JsonSerializable(typeof(RuntimeStream))]
[JsonSerializable(typeof(RuntimeSecure))]
[JsonSerializable(typeof(int))]
sealed partial class RuntimeJsonContext : JsonSerializerContext;
