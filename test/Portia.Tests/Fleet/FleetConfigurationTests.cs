namespace Cntryl.Portia;

/// <summary>Fleet configuration rejects durations the pinned Fitz SDK cannot schedule.</summary>
public sealed class FleetConfigurationTests
{
    /// <summary>An invalid TTL is rejected at registration rather than inside acquisition retries.</summary>
    [Fact]
    public async Task ShouldRejectConfigurationGivenLeaseTtlNotSupportedByFitz()
    {
        // Arrange
        var options = new FleetRunOptions { MembershipSelector = "lease://audit/members/*", WorkerId = "w", LeaseTtl = TimeSpan.FromDays(60) };
        await using var client = new FitzClient(new ClientConfig(new Uri("ws://127.0.0.1:1")));
        var sdkFailure = await Record.ExceptionAsync(() => client.Lease.WithLeaseAsync("lease://test/members/w",
            options.LeaseTtl, _ => ValueTask.CompletedTask).WaitAsync(TimeSpan.FromSeconds(3)));
        var fitz = new PortiaFitzBuilder(new ServiceCollection().AddPortia());

        // Act
        var configurationFailure = Record.Exception(() => fitz.UseFleet(options));

        // Assert
        var rejection = Assert.IsType<LeaseException>(sdkFailure);
        Assert.Contains("schedulable", rejection.Message, StringComparison.Ordinal);
        Assert.IsType<ArgumentOutOfRangeException>(configurationFailure);
    }

    /// <summary>Rounding a fractional TTL beyond the SDK maximum must fail during configuration.</summary>
    [Fact]
    public void ShouldRejectConfigurationGivenLeaseTtlExceedsMaximumByOneTick()
    {
        // Arrange
        var ttl = TimeSpan.FromSeconds(uint.MaxValue / 1000) + TimeSpan.FromTicks(1);
        var options = new FleetRunOptions { MembershipSelector = "lease://test/members/*", LeaseTtl = ttl };
        var fitz = new PortiaFitzBuilder(new ServiceCollection().AddPortia());

        // Act
        var failure = Record.Exception(() => fitz.UseFleet(options));

        // Assert
        var invalid = Assert.IsType<ArgumentOutOfRangeException>(failure);
        Assert.Equal(nameof(FleetRunOptions.LeaseTtl), invalid.ParamName);
        Assert.Equal(ttl, invalid.ActualValue);
    }

    /// <summary>The smallest positive TTL and the exact schedulable maximum remain valid configurations.</summary>
    [Theory]
    [InlineData(1L)]
    [InlineData(42_949_670_000_000L)]
    public void ShouldAcceptConfigurationGivenSchedulableLeaseTtl(long ticks)
    {
        // Arrange
        var options = new FleetRunOptions { MembershipSelector = "lease://test/members/*", LeaseTtl = TimeSpan.FromTicks(ticks) };
        var fitz = new PortiaFitzBuilder(new ServiceCollection().AddPortia());

        // Act
        var failure = Record.Exception(() => fitz.UseFleet(options));

        // Assert
        Assert.Null(failure);
        Assert.Same(options, fitz.Fleet);
    }
}
