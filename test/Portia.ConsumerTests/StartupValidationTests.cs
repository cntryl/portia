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
        _ = Assert.ThrowsAny<ArgumentException>(() => services.AddPortiaProjectorRunner<FirstProjector>(
            new ProjectionRunOptions { MaxBatchSize = batchSize, RebuildId = rebuildId }));
        Assert.Empty(services);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DirectHostsRejectInvalidPollingIntervals(int milliseconds)
    {
        var reader = new InMemoryEventStore();
        _ = Assert.ThrowsAny<ArgumentException>(() => new ReactorHostedService(new ReactorRunner(reader),
            new TestReactor(), pollInterval: TimeSpan.FromMilliseconds(milliseconds)));
        _ = Assert.ThrowsAny<ArgumentException>(() => new ProjectorHostedService(new ProjectorRunner(reader),
            new TestProjection(), pollInterval: TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public void DirectProjectorHostRejectsInvalidBatchBeforeStarting()
    {
        _ = Assert.ThrowsAny<ArgumentException>(() => new ProjectorHostedService(new ProjectorRunner(new InMemoryEventStore()),
            new TestProjection(), new ProjectionRunOptions { MaxBatchSize = 0 }));
    }

    sealed class TestReactor() : BaseReactor(new InMemoryProjectionCheckpointStore(), EventStreamPattern.ForPattern("test"), "test");
    sealed class TestProjection() : BaseProjector(new Target(), EventStreamPattern.ForPattern("test"), "test");
    sealed class Target : IProjectionStore
    {
        public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
