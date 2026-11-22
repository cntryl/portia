using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Verifies that the bus refuses a dispatch its composed registrations cannot serve with an error
///     that names the application's own mistake, before any application component runs.
/// </summary>
public sealed class RequestDispatchShapeTests
{
    /// <summary>
    ///     Verifies that a request handled with a result names its registered shape when it is
    ///     dispatched as a no-result request, instead of surfacing an internal cast failure.
    /// </summary>
    [Fact]
    public async Task ShouldNameHandledShapeGivenResultRequestDispatchedWithoutResult()
    {
        // Arrange
        await using var provider = new ServiceCollection().AddScoped<TwoShapeRequestHandler>().BuildServiceProvider();
        var bus = new RequestBus(provider, new RequestRegistry(
            [new RequestRegistration<TwoShapeRequest, TwoShapeRequestHandler, int>()], [], [], []));
        IRequest request = new TwoShapeRequest();

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await bus.DispatchAsync(request, bus.CreateContext(RequestActor.System), TestContext.Current.CancellationToken));

        // Assert
        var error = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains(typeof(TwoShapeRequest).FullName!, error.Message, StringComparison.Ordinal);
        Assert.Contains("IRequest<System.Int32>", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that a request handled without a result names its registered shape when it is
    ///     dispatched for a result.
    /// </summary>
    [Fact]
    public async Task ShouldNameHandledShapeGivenNoResultRequestDispatchedForResult()
    {
        // Arrange
        await using var provider = new ServiceCollection().AddScoped<TwoShapeRequestHandler>().BuildServiceProvider();
        var bus = new RequestBus(provider, new RequestRegistry(
            [new RequestRegistration<TwoShapeRequest, TwoShapeRequestHandler>()], [], [], []));
        IRequest<int> request = new TwoShapeRequest();

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await bus.DispatchAsync(request, bus.CreateContext(RequestActor.System), TestContext.Current.CancellationToken));

        // Assert
        var error = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains(typeof(TwoShapeRequest).FullName!, error.Message, StringComparison.Ordinal);
        Assert.Contains("handled as IRequest,", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies that a stream dispatch rejects a missing request when it is called, like the unary
    ///     dispatches, rather than when the returned sequence is first enumerated.
    /// </summary>
    [Fact]
    public async Task ShouldRejectMissingRequestGivenStreamDispatchNotYetEnumerated()
    {
        // Arrange
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var bus = new RequestBus(provider, new RequestRegistry([], [], [], []));
        var context = bus.CreateContext(RequestActor.System);

        // Act
        var exception = Record.Exception(() => bus.DispatchStreamAsync<int>(null!, context, TestContext.Current.CancellationToken));

        // Assert
        _ = Assert.IsType<ArgumentNullException>(exception);
    }

    /// <summary>
    ///     Verifies that a declared permission that renders blank refuses the request without consulting
    ///     the evaluator, so an evaluator that treats an unknown permission leniently cannot grant it.
    /// </summary>
    /// <param name="permission">The blank permission the registration renders.</param>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ShouldRefuseRequestGivenBlankDeclaredPermission(string permission)
    {
        // Arrange
        var handler = new UniversalActionHandler();
        var evaluator = TestPermissionEvaluator.AllowAll();
        await using var provider = new ServiceCollection().AddSingleton(handler)
            .AddSingleton<IPermissionEvaluator>(evaluator).BuildServiceProvider();
        var bus = new RequestBus(provider, new RequestRegistry(
            [new RequestRegistration<UniversalAction, UniversalActionHandler>(_ => permission)], [], [], []));

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await bus.DispatchAsync(new UniversalAction(1), bus.CreateContext(RequestActor.System), TestContext.Current.CancellationToken));

        // Assert
        var error = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains(typeof(UniversalAction).FullName!, error.Message, StringComparison.Ordinal);
        Assert.Empty(evaluator.EvaluatedPermissions);
        Assert.Empty(handler.HandledValues);
    }
}
