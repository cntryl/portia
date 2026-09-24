using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Consumer;

public sealed class StartupValidationTests
{
    [Fact]
    public void AddPortiaComposesJsonContextOwnedByReferencedAssembly()
    {
        var services = new ServiceCollection();
        _ = ReferencedJsonComposition.Add(services);
        using var provider = services.BuildServiceProvider();

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
    public void ApplicationJsonOptionsNeverBecomePortiasWireFormat(bool registeredFirst)
    {
        var application = new JsonSerializerOptions { WriteIndented = true };
        var services = new ServiceCollection();
        if (registeredFirst)
            _ = services.AddSingleton(application);
        _ = ReferencedJsonComposition.Add(services);
        if (!registeredFirst)
            _ = services.AddSingleton(application);
        using var provider = services.BuildServiceProvider();

        var portia = provider.GetRequiredKeyedService<JsonSerializerOptions>(PortiaServiceKeys.Json);
        Assert.NotSame(application, portia);
        Assert.True(portia.TryGetTypeInfo(typeof(ReferencedContractJsonPayload), out _));
        Assert.Same(application, provider.GetRequiredService<JsonSerializerOptions>());
    }

    [Fact]
    public async Task AdvertisedUsageStillFailsStartupWhenNoComposedContextResolvesTheRoot()
    {
        var builder = Host.CreateApplicationBuilder();
        _ = builder.Services.AddPortia()
            .AddGeneratedHandler(new RequestRegistration<MissingJsonMetadataRequest, MissingJsonMetadataHandler>());
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains(typeof(MissingJsonMetadataRequest).FullName!, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(1, "")]
    [InlineData(1, " ")]
    public void InvalidProjectionOptionsDoNotMutateRegistrations(int batchSize, string? rebuildId)
    {
        var services = new ServiceCollection();
        _ = Assert.ThrowsAny<ArgumentException>(() =>
            services.AddPortia().AddProjector<FirstProjector>("first-projector", WorkloadScope.Global,
                o => o.Processing = new ProjectionRunOptions { MaxBatchSize = batchSize, RebuildId = rebuildId }));
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(WorkloadRegistration));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WorkloadsRejectInvalidPollingIntervals(int milliseconds)
    {
        var services = new ServiceCollection();
        _ = Assert.ThrowsAny<ArgumentException>(() =>
            services.AddPortia().AddReactor<FirstReactor>("first-reactor", WorkloadScope.Global,
                o => o.PollInterval = TimeSpan.FromMilliseconds(milliseconds)));
        _ = Assert.ThrowsAny<ArgumentException>(() =>
            services.AddPortia().AddProjector<FirstProjector>("first-projector", WorkloadScope.Global,
                o => o.PollInterval = TimeSpan.FromMilliseconds(milliseconds)));
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(WorkloadRegistration));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WorkloadsRejectInvalidFailureAttemptLimits(int attempts)
    {
        var services = new ServiceCollection();
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => services.AddPortia()
            .AddProjector<FirstProjector>("first-projector", WorkloadScope.Global,
                options => options.FailureAttemptLimit = attempts));
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(WorkloadRegistration));
    }

    [Fact]
    public void WorkloadsRejectInvalidMaximumFailureDelay()
    {
        var services = new ServiceCollection();
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => services.AddPortia()
            .AddProjector<FirstProjector>("first-projector", WorkloadScope.Global,
                options => options.MaximumFailureDelay = TimeSpan.Zero));
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(WorkloadRegistration));
    }

    [Fact]
    public void ReactorsRejectRebuildGenerations()
    {
        var services = new ServiceCollection();
        _ = Assert.ThrowsAny<ArgumentException>(() =>
            services.AddPortia().AddReactor<FirstReactor>("first-reactor", WorkloadScope.Global,
                o => o.Processing = new ProjectionRunOptions { RebuildId = "repair" }));
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(WorkloadRegistration));
    }

    sealed record MissingJsonMetadataRequest : IRequest;

    sealed class MissingJsonMetadataHandler : IRequestHandler<MissingJsonMetadataRequest>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<MissingJsonMetadataRequest> context,
            CancellationToken ct) => ValueTask.FromResult(Result.Success);
    }
}
