namespace Cntryl.Portia;

/// <summary>
/// Verifies <see cref="RequestDispatch" /> — the actor re-validation + bus dispatch extracted out
/// of <see cref="QueueRunner" /> and <see cref="RequestNotificationRunner" /> so a third
/// request-consuming transport doesn't have to reimplement it a third time.
/// </summary>
public sealed class RequestDispatchTests
{
    /// <summary>
    /// Verifies that a request whose actor token fails re-validation is never dispatched to the
    /// bus at all and returns the validator's failure unchanged.
    /// </summary>
    [Fact]
    public async Task ShouldReturnValidationFailureWithoutDispatchingWhenActorValidationFails()
    {
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(changeValueHandler: handler);
        var bus = busHost.Bus;
        var validator = new TestRequestActorValidator(rejectToken: "expired-token");

        var dispatch = await RequestDispatch.SendAsync(
            validator, bus, new ChangeValue(1), "expired-token", new QueueInvocation("queue://test/work/item", 1), RequestMetadata.Create());

        Assert.False(dispatch.WasDispatched);
        Assert.False(dispatch.Outcome.IsSuccess);
        Assert.Equal("Token expired.", dispatch.Outcome.Error!.Message);
        Assert.Null(handler.LastValue);
    }

    /// <summary>
    /// Verifies that a request whose actor token re-validates successfully is dispatched to the
    /// bus, and the bus's own result is returned.
    /// </summary>
    [Fact]
    public async Task ShouldDispatchToBusAndReturnItsResultWhenActorValidationSucceeds()
    {
        var handler = new ChangeValueHandler();
        using var busHost = TestRequestBus.Create(changeValueHandler: handler);
        var bus = busHost.Bus;
        var validator = new TestRequestActorValidator();

        var dispatch = await RequestDispatch.SendAsync(
            validator, bus, new ChangeValue(42), "valid-token", new QueueInvocation("queue://test/work/item", 1), RequestMetadata.Create());

        Assert.True(dispatch.WasDispatched);
        Assert.True(dispatch.Outcome.IsSuccess);
        Assert.Equal(42, handler.LastValue);
    }
}
