namespace Cntryl.Portia;

/// <summary>Failed HTTP validation continues to block worker effects until startup is abandoned.</summary>
public sealed class PortiaStartupValidationRegistryTests
{
    /// <summary>Completing the remaining registrations cannot turn a failed gate into permission.</summary>
    [Fact]
    public async Task ShouldKeepFailedValidationBlockingAfterOtherRegistrationsComplete()
    {
        var registry = new PortiaStartupValidationRegistry();
        var failed = registry.BeginEndpointValidation();
        var remaining = registry.BeginEndpointValidation();
        var failure = new InvalidOperationException("Invalid endpoint authorization.");

        failed.Fail(failure);
        remaining.Complete();

        Assert.True(registry.HasPendingEndpointValidations);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.WaitForEndpointValidationsAsync(CancellationToken.None));
        Assert.Same(failure, error);
    }

    /// <summary>A later successful mapping does not erase an earlier endpoint failure.</summary>
    [Fact]
    public async Task ShouldPreserveFailureAcrossLaterRegistrations()
    {
        var registry = new PortiaStartupValidationRegistry();
        var failure = new InvalidOperationException("Invalid endpoint authorization.");
        registry.BeginEndpointValidation().Fail(failure);

        registry.BeginEndpointValidation().Complete();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.WaitForEndpointValidationsAsync(CancellationToken.None));
        Assert.Same(failure, error);
    }
}
