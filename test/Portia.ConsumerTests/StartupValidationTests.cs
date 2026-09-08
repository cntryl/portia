using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

public sealed class StartupValidationTests
{
    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(1, "")]
    [InlineData(1, " ")]
    public void InvalidProjectionOptionsDoNotMutateRegistrations(int batchSize, string? rebuildId)
    {
        var services = new ServiceCollection();
        _ = Assert.ThrowsAny<ArgumentException>(() => services.AddPortia().AddProjector<FirstProjector>(WorkloadScope.Global, o => o.Processing = new ProjectionRunOptions { MaxBatchSize = batchSize, RebuildId = rebuildId }));
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(WorkloadRegistration));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WorkloadsRejectInvalidPollingIntervals(int milliseconds)
    {
        var services = new ServiceCollection();
        _ = Assert.ThrowsAny<ArgumentException>(() => services.AddPortia().AddReactor<FirstReactor>(WorkloadScope.Global, o => o.PollInterval = TimeSpan.FromMilliseconds(milliseconds)));
        _ = Assert.ThrowsAny<ArgumentException>(() => services.AddPortia().AddProjector<FirstProjector>(WorkloadScope.Global, o => o.PollInterval = TimeSpan.FromMilliseconds(milliseconds)));
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(WorkloadRegistration));
    }

    [Fact]
    public void ReactorsRejectRebuildGenerations()
    {
        var services = new ServiceCollection();
        _ = Assert.ThrowsAny<ArgumentException>(() => services.AddPortia().AddReactor<FirstReactor>(WorkloadScope.Global, o => o.Processing = new ProjectionRunOptions { RebuildId = "repair" }));
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(WorkloadRegistration));
    }
}
