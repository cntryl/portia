using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
/// Verifies the <c>SendAsync</c>/<c>StreamAsync</c> overloads that moved off <see cref="IRequestBus" />
/// into <see cref="RequestBusExtensions" /> behave exactly as the interface members did.
/// </summary>
public sealed class RequestBusExtensionsTests
{
    /// <summary>
    /// Regression test for the narrowing of <see cref="IRequestBus" />: the six convenience
    /// overloads used to exist twice — as interface default implementations that built a context
    /// with no <see cref="TimeProvider" />, and as <c>RequestBus</c> overrides that passed the
    /// registered one. Building the context through <see cref="IRequestBus.CreateContext" />
    /// removes that fork, so a registered clock is honoured no matter which overload is called.
    /// </summary>
    [Fact]
    public async Task ShouldStampExecutionStartFromTheRegisteredTimeProvider()
    {
        var clock = new FixedClock(new DateTimeOffset(2031, 4, 5, 6, 7, 8, TimeSpan.Zero));
        var handler = new ClockProbeHandler();

        var services = new ServiceCollection();
        _ = services.AddSingleton<TimeProvider>(clock);
        _ = services.AddSingleton(handler);
        _ = services.AddPortia(p => p.AddClockProbeHandler());
        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();

        Assert.True((await bus.SendAsync(new ClockProbe(), RequestActor.System)).IsSuccess);
        Assert.Equal(clock.GetUtcNow(), handler.StartedAt);

        // The child-context overload has to honour it too.
        handler.StartedAt = null;
        var parent = bus.CreateContext(RequestActor.System);
        Assert.True((await bus.SendAsync(new ClockProbe(), parent)).IsSuccess);
        Assert.Equal(clock.GetUtcNow(), handler.StartedAt);
        Assert.Equal(parent.CorrelationId, handler.CorrelationId);
    }

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>A request used to observe the execution context a dispatch runs under.</summary>
public sealed record ClockProbe : IRequest;

sealed class ClockProbeHandler : IRequestHandler<ClockProbe>
{
    public DateTimeOffset? StartedAt { get; set; }

    public Uuid CorrelationId { get; private set; }

    public ValueTask<Result> HandleAsync(IRequestContext<ClockProbe> context, CancellationToken ct)
    {
        StartedAt = context.StartedAt;
        CorrelationId = context.CorrelationId;
        return ValueTask.FromResult(Result.Success);
    }
}
