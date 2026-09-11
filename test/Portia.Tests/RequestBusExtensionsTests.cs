using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Verifies the <c>SendAsync</c>/<c>StreamAsync</c> overloads that moved off <see cref="IRequestBus" />
///     into <see cref="RequestBusExtensions" /> behave exactly as the interface members did.
/// </summary>
public sealed class RequestBusExtensionsTests
{
    /// <summary>A reaction command inherits the triggering event's execution identity.</summary>
    [Fact]
    public async Task ShouldPreserveReactionCausalityWhenReactionCommandSucceeds()
    {
        var source = CreateReactionContext();
        var bus = new RecordingRequestBus(Result.Success);

        await bus.SendReactionAsync(new ClockProbe(), source);

        Assert.NotNull(bus.Context);
        Assert.Equal(source.CorrelationId, bus.Context.CorrelationId);
        Assert.Equal(source.CauseId, bus.Context.CausationId);
        Assert.True(RequestActor.IsSystem(bus.Context.Actor));
    }

    /// <summary>A failed command faults the reaction instead of allowing its checkpoint to advance.</summary>
    [Fact]
    public async Task ShouldThrowReactionCommandFailureGivenFailedCommandResult()
    {
        var error = new RequestError(RequestErrorKind.Conflict, "account is closed");
        var bus = new RecordingRequestBus(Result.Failure(error));

        var exception = await Assert.ThrowsAsync<ReactionCommandFailedException>(() =>
            bus.SendReactionAsync(new ClockProbe(), CreateReactionContext()).AsTask());

        Assert.Same(error, exception.Error);
        Assert.DoesNotContain(error.Message, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Regression test for the narrowing of <see cref="IRequestBus" />: the six convenience
    ///     overloads used to exist twice — as interface default implementations that built a context
    ///     with no <see cref="TimeProvider" />, and as <c>RequestBus</c> overrides that passed the
    ///     registered one. Building the context through <see cref="IRequestBus.CreateContext" />
    ///     removes that fork, so a registered clock is honoured no matter which overload is called.
    /// </summary>
    [Fact]
    public async Task ShouldStampExecutionStartFromTheRegisteredTimeProvider()
    {
        var clock = new FixedClock(new DateTimeOffset(2031, 4, 5, 6, 7, 8, TimeSpan.Zero));
        var handler = new ClockProbeHandler();

        var services = new ServiceCollection();
        _ = services.AddSingleton<TimeProvider>(clock);
        _ = services.AddSingleton(handler);
        _ = services.AddPortia().AddRequestHandler<ClockProbeHandler>();
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

    static ReactionExecutionContext CreateReactionContext()
    {
        var aggregateId = Uuid.CreateVersion4();
        var ev = new ValueChanged(1);
        ev.AttachMetadata(new DomainEventMetadata(
            Uuid.CreateVersion4(), aggregateId, 1, DateTimeOffset.UtcNow, Uuid.CreateVersion4()));
        return new ReactionExecutionContext(
            new DomainEventRecord(new EventStreamAddress("test", "reactions", aggregateId.ToString()), ev, 0, 0, 0),
            RequestActor.System);
    }

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    sealed class RecordingRequestBus(Result outcome) : IRequestBus
    {

        public ValueTask<Result> AuthorizeAsync(IRequestBase request, RequestDispatchContext context,
            CancellationToken ct = default) => ValueTask.FromResult(Result.Success);
        public RequestDispatchContext? Context { get; private set; }

        public RequestDispatchContext CreateContext(ClaimsPrincipal actor, RequestMetadata? metadata = null) =>
            new(actor, metadata: metadata);

        public ValueTask<Result> DispatchAsync(IRequest request, RequestDispatchContext context,
            CancellationToken ct = default)
        {
            Context = context;
            return ValueTask.FromResult(outcome);
        }

        public ValueTask<Result<TOut>> DispatchAsync<TOut>(IRequest<TOut> request, RequestDispatchContext context,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TOut> DispatchStreamAsync<TOut>(IStreamRequest<TOut> request,
            RequestDispatchContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
