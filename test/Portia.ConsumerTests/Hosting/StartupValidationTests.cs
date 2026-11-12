using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Consumer;

public sealed class StartupValidationTests
{
    [Fact]
    public void ShouldComposeJsonContextGivenContextOwnedByReferencedAssembly()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = ReferencedJsonComposition.Add(services);

        // Act
        using var provider = services.BuildServiceProvider();

        // Assert
        Assert.True(provider.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json)
            .TryGetTypeInfo(typeof(ReferencedContractJsonPayload), out _));
    }

    /// <summary>
    ///     An application's own <see cref="JsonSerializerOptions" /> registration, made before or after Portia's,
    ///     never becomes Portia's wire format, and Portia never replaces it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ShouldKeepPortiaWireFormatGivenApplicationJsonOptionsRegistered(bool registeredFirst)
    {
        // Arrange
        var application = new JsonSerializerOptions { WriteIndented = true };
        var services = new ServiceCollection();
        if (registeredFirst)
            _ = services.AddSingleton(application);
        _ = ReferencedJsonComposition.Add(services);
        if (!registeredFirst)
            _ = services.AddSingleton(application);
        using var provider = services.BuildServiceProvider();

        // Act
        var portia = provider.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json);

        // Assert
        Assert.NotSame(application, portia);
        Assert.True(portia.TryGetTypeInfo(typeof(ReferencedContractJsonPayload), out _));
        Assert.Same(application, provider.GetRequiredService<JsonSerializerOptions>());
    }

    [Fact]
    public async Task ShouldFailStartupGivenNoComposedContextResolvingRoot()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();
        _ = builder.Services.AddPortia()
            .AddGeneratedHandler(new RequestRegistration<MissingJsonMetadataRequest, MissingJsonMetadataHandler>());

        // Act
        using var host = builder.Build();

        // Assert
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains(typeof(MissingJsonMetadataRequest).FullName!, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(1, "")]
    [InlineData(1, " ")]
    public void ShouldNotMutateRegistrationsGivenInvalidProjectionOptions(int batchSize, string? rebuildId)
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var error = Record.Exception(() =>
            services.AddPortia().AddProjector<FirstProjector>("first-projector", WorkloadScope.Global,
                o => o.Processing = new ProjectionRunOptions { MaxBatchSize = batchSize, RebuildId = rebuildId }));

        // Assert
        _ = Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(WorkloadRegistration));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ShouldRejectWorkloadGivenInvalidPollingInterval(int milliseconds)
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var reactorError = Record.Exception(() =>
            services.AddPortia().AddReactor<FirstReactor>("first-reactor", WorkloadScope.Global,
                o => o.PollInterval = TimeSpan.FromMilliseconds(milliseconds)));
        var projectorError = Record.Exception(() =>
            services.AddPortia().AddProjector<FirstProjector>("first-projector", WorkloadScope.Global,
                o => o.PollInterval = TimeSpan.FromMilliseconds(milliseconds)));

        // Assert
        _ = Assert.IsAssignableFrom<ArgumentException>(reactorError);
        _ = Assert.IsAssignableFrom<ArgumentException>(projectorError);
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(WorkloadRegistration));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ShouldRejectWorkloadGivenInvalidFailureAttemptLimit(int attempts)
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var error = Record.Exception(() => services.AddPortia()
            .AddProjector<FirstProjector>("first-projector", WorkloadScope.Global,
                options => options.FailureAttemptLimit = attempts));

        // Assert
        _ = Assert.IsType<ArgumentOutOfRangeException>(error);
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(WorkloadRegistration));
    }

    [Fact]
    public void ShouldRejectWorkloadGivenInvalidMaximumFailureDelay()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var error = Record.Exception(() => services.AddPortia()
            .AddProjector<FirstProjector>("first-projector", WorkloadScope.Global,
                options => options.MaximumFailureDelay = TimeSpan.Zero));

        // Assert
        _ = Assert.IsType<ArgumentOutOfRangeException>(error);
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(WorkloadRegistration));
    }

    [Fact]
    public void ShouldRejectReactorGivenRebuildGeneration()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var error = Record.Exception(() =>
            services.AddPortia().AddReactor<FirstReactor>("first-reactor", WorkloadScope.Global,
                o => o.Processing = new ProjectionRunOptions { RebuildId = "repair" }));

        // Assert
        _ = Assert.IsAssignableFrom<ArgumentException>(error);
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(WorkloadRegistration));
    }

    sealed record MissingJsonMetadataRequest : IRequest;

    sealed class MissingJsonMetadataHandler : IRequestHandler<MissingJsonMetadataRequest>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<MissingJsonMetadataRequest> context,
            CancellationToken ct) => ValueTask.FromResult(Result.Success);
    }
}
