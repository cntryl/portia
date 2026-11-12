using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

/// <summary>
///     Verifies what <c>AddFitz(configuration)</c> refuses. Every value here arrives from appsettings or
///     the environment, so a typo is the normal case rather than the exceptional one — and each of these
///     would otherwise surface much later as a connection that never succeeds, a startup that hangs for
///     an unintended length of time, or a fleet whose members never find each other.
/// </summary>
public sealed class FitzConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("127.0.0.1:4090")]
    [InlineData("http://127.0.0.1:4090/")]
    [InlineData("https://127.0.0.1:4090/")]
    [InlineData("/relative/ws")]
    public void EndpointMustBeAnAbsoluteWebSocketUri(string? endpoint)
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var error = Record.Exception(() =>
            services.AddPortia().AddFitz(Configuration(("Endpoint", endpoint))));

        // Assert
        var invalid = Assert.IsType<ArgumentException>(error);
        Assert.Contains("absolute ws:// or wss:// URI", invalid.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ws://127.0.0.1:4090/ws")]
    [InlineData("wss://broker.example/ws")]
    public void AcceptsEitherWebSocketScheme(string endpoint)
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        _ = services.AddPortia().AddFitz(Configuration(("Endpoint", endpoint)));

        // Assert
        Assert.Contains(services, item => item.ServiceType.Name == "FitzApplicationConnection");
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("3601")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void StartupTimeoutMustBePositiveFiniteAndWithinAnHour(string seconds)
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var error = Record.Exception(() => services.AddPortia().AddFitz(
            Configuration(("Endpoint", "ws://127.0.0.1:4090/ws"), ("StartupTimeoutSeconds", seconds))));

        // Assert
        var invalid = Assert.IsType<ArgumentException>(error);
        Assert.Contains("between zero and 3600 seconds", invalid.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.5")]
    [InlineData("30")]
    [InlineData("3600")]
    public void StartupTimeoutAcceptsAnyPositiveValueWithinAnHour(string seconds)
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        _ = services.AddPortia().AddFitz(
            Configuration(("Endpoint", "ws://127.0.0.1:4090/ws"), ("StartupTimeoutSeconds", seconds)));

        // Assert
        Assert.Contains(services, item => item.ServiceType.Name == "FitzApplicationConnection");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("two words")]
    [InlineData("has/slash")]
    [InlineData("star*")]
    public void ApplicationNameMustBeAnExactRouteSegmentBecauseItBecomesTheFleetSelector(string name)
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var error = Record.Exception(() => services.AddPortia().AddFitz(
            Configuration(("Endpoint", "ws://127.0.0.1:4090/ws"), ("ApplicationName", name))));

        // Assert
        var invalid = Assert.IsType<ArgumentException>(error);
        Assert.Contains("exact route segment", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplicationNameBecomesTheFleetMembershipSelector()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        _ = services.AddPortia().AddFitz(
            Configuration(("Endpoint", "ws://127.0.0.1:4090/ws"), ("ApplicationName", "orders")));

        // Assert
        Assert.Contains(services, item => item.ServiceType.Name == "FitzApplicationConnection");
    }

    [Fact]
    public void SecondFitzConnectionWithDifferentSettingsIsRejected()
    {
        // Arrange
        var services = new ServiceCollection();
        var portia = services.AddPortia();

        // Act
        _ = portia.AddFitz(Configuration(("Endpoint", "ws://127.0.0.1:4090/ws")));

        // Assert
        var error = Assert.Throws<InvalidOperationException>(() =>
            portia.AddFitz(Configuration(("Endpoint", "ws://127.0.0.1:4091/ws"))));

        Assert.Contains("conflicting Fitz connections", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatingTheSameFitzConfigurationComposesOntoTheOneConnection()
    {
        // Arrange
        var services = new ServiceCollection();
        var portia = services.AddPortia();
        _ = portia.AddFitz(Configuration(("Endpoint", "ws://127.0.0.1:4090/ws")));
        var count = services.Count(item => item.ServiceType.Name == "FitzApplicationConnection");

        // Act
        _ = portia.AddFitz(Configuration(("Endpoint", "ws://127.0.0.1:4090/ws")));

        // Assert
        Assert.Equal(count, services.Count(item => item.ServiceType.Name == "FitzApplicationConnection"));
    }

    [Fact]
    public void AnOwnedConnectionAndACallerSuppliedClientCannotBothOwnOneApplication()
    {
        // Arrange
        var services = new ServiceCollection();
        var portia = services.AddPortia();

        // Act
        _ = portia.AddFitz(Configuration(("Endpoint", "ws://127.0.0.1:4090/ws")));

        // Assert
        var error = Assert.Throws<InvalidOperationException>(() =>
            portia.UseFitzClient(new Client(new ClientConfig(new Uri("ws://127.0.0.1:4090/ws"))), _ => { }));

        Assert.Contains("conflicting Fitz connections", error.Message, StringComparison.Ordinal);
    }

    static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(item => item.Key, item => item.Value))
            .Build();
}
