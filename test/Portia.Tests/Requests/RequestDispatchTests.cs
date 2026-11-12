namespace Cntryl.Portia;

/// <summary>
///     Verifies <see cref="RequestDispatch" /> — the actor re-validation + bus dispatch extracted out
///     of <see cref="QueueRunner" /> and <see cref="RequestNotificationRunner" /> so a third
///     request-consuming transport doesn't have to reimplement it a third time.
/// </summary>
public sealed class RequestDispatchTests
{
    /// <summary>Dispatches both request shapes from one delivery description.</summary>
    [Fact]
    public async Task ShouldDispatchBothRequestShapesGivenOneDelivery()
    {
        // Arrange
        using var busHost = TestRequestBus.Create();
        using var cancellation = new CancellationTokenSource();
        var invocation = new QueueInvocation("queue://test/work/item", 1);

        var command = await RequestDispatch.SendAsync(
            new TestRequestActorValidator(), busHost.Bus, new ChangeValue(1),
            new RequestDelivery("test.change_value", invocation, RequestMetadata.Create(), "valid-token"),
            cancellation.Token);

        // Act
        var query = await RequestDispatch.SendAsync(
            new TestRequestActorValidator(), busHost.Bus, new GetValue(),
            new RequestDelivery("test.get_value", invocation, RequestMetadata.Create(), "valid-token"),
            cancellation.Token);

        // Assert
        Assert.True(command.WasDispatched);
        Assert.True(query.WasDispatched);
    }

    /// <summary>
    ///     Verifies that a request whose actor token fails re-validation is never dispatched to the
    ///     bus at all and returns the validator's failure unchanged.
    /// </summary>
    [Fact]
    public async Task ShouldReturnValidationFailureWithoutDispatchingWhenActorValidationFails()
    {
        // Arrange
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var bus = busHost.Bus;
        var validator = new TestRequestActorValidator("expired-token");

        // Act
        var dispatch = await RequestDispatch.SendAsync(validator, bus, new ChangeValue(1),
            new RequestDelivery("test.change_value", new QueueInvocation("queue://test/work/item", 1),
                RequestMetadata.Create(), "expired-token"));

        // Assert
        Assert.False(dispatch.WasDispatched);
        Assert.False(dispatch.Outcome.IsSuccess);
        Assert.Equal("Token expired.", dispatch.Outcome.Error.Message);
        Assert.Null(handler.LastValue);
    }

    /// <summary>
    ///     Verifies that a request whose actor token re-validates successfully is dispatched to the
    ///     bus, and the bus's own result is returned.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchToBusAndReturnItsResultWhenActorValidationSucceeds()
    {
        // Arrange
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(handler);
        var bus = busHost.Bus;
        var validator = new TestRequestActorValidator();

        // Act
        var dispatch = await RequestDispatch.SendAsync(validator, bus, new ChangeValue(42),
            new RequestDelivery("test.change_value", new QueueInvocation("queue://test/work/item", 1),
                RequestMetadata.Create(), "valid-token"));

        // Assert
        Assert.True(dispatch.WasDispatched);
        Assert.True(dispatch.Outcome.IsSuccess);
        Assert.Equal(42, handler.LastValue);
    }
}
