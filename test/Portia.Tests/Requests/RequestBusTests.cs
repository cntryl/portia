namespace Cntryl.Portia;

/// <summary>
///     Verifies generated request dispatch through the request bus.
/// </summary>
public sealed class RequestBusTests
{
    /// <summary>
    ///     Verifies that a no-result request is dispatched to its single registered handler.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchRequestGivenRequestWithNoResult()
    {
        // Arrange
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var bus = busHost.Bus;

        // Act
        var result = await bus.SendAsync(new ChangeValue(42), RequestActor.System);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(42, handler.LastValue);
    }

    /// <summary>
    ///     Verifies that a request with a declared result type is dispatched and returns the
    ///     handler's result.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchRequestGivenRequestWithResult()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        var bus = busHost.Bus;

        // Act
        var result = await bus.SendAsync(new GetValue(), RequestActor.System);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);
    }
}
