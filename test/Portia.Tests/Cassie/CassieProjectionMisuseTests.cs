using Cntryl.Cassie.Connection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>Checks that Cassie repository misuse fails permanently and that registration mismatches fail at startup.</summary>
public sealed partial class CassieProjectionMisuseTests
{
    const string UnreachableServer = "Host=127.0.0.1;Port=1;Database=postgres;Username=root;Password=unused";
    static readonly CheckpointIdentity Identity = new("Probe", EventStreamPattern.ForPattern("misuse", "accounts"));

    /// <summary>Reading through the query path inside an open batch is a defect a retry cannot fix.</summary>
    [Fact]
    public async Task ShouldClassifyMisuseAsPermanentGivenQueryReadDuringBatch()
    {
        // Arrange
        var activity = new CassieProjectionActivityGate();

        // Act
        await activity.EnterBatchAsync(default);

        // Assert
        var misuse = Assert.Throws<NotSupportedException>(() => activity.EnterRead());

        Assert.False(Diagnose(misuse).IsTransient);
    }

    /// <summary>A second batch on the same repository instance is a defect a retry cannot fix.</summary>
    [Fact]
    public async Task ShouldClassifyMisuseAsPermanentGivenOverlappingBatches()
    {
        // Arrange
        var activity = new CassieProjectionActivityGate();

        // Act
        await activity.EnterBatchAsync(default);

        // Assert
        var misuse = await Assert.ThrowsAsync<NotSupportedException>(() => activity.EnterBatchAsync(default).AsTask());

        Assert.False(Diagnose(misuse).IsTransient);
    }

    /// <summary>Writing outside a projection batch is a defect a retry cannot fix.</summary>
    [Fact]
    public async Task ShouldClassifyMisuseAsPermanentGivenWriteOutsideBatch()
    {
        // Arrange
        await using var source = new CassieDataSource(UnreachableServer);
        using var database = new CassieProjectionDatabase(source);

        // Act
        await using var repository = new ProbeRepository(database, "Probe");

        // Assert
        var misuse = Assert.Throws<NotSupportedException>(() => repository.Write());

        Assert.False(Diagnose(misuse).IsTransient);
    }

    /// <summary>A repository constructed for another projector stops the host before any batch runs.</summary>
    [Fact]
    public async Task ShouldRejectStartupGivenRepositoryNamedForAnotherProjector()
    {
        // Arrange
        var services = new ServiceCollection();
        _ = services.AddSingleton<IDomainEventReader>(new InMemoryEventStore());
        _ = services.AddScoped(sp => new ProbeRepository(sp.GetRequiredService<CassieProjectionDatabase>(), "SomethingElse"));
        _ = services.AddPortia().AddCassie(UnreachableServer)
            .AddCassieProjection<ProbeProjector, ProbeRepository>("Probe", WorkloadScope.Global)
            .AddWorkers().UseSingleProcessWorkloads();

        // Act
        await using var provider = services.BuildServiceProvider();

        // Assert
        var worker = Assert.IsAssignableFrom<IHostedLifecycleService>(
            Assert.Single(provider.GetServices<IHostedService>().OfType<BackgroundService>()));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => worker.StartingAsync(default));

        Assert.Contains("SomethingElse", error.Message, StringComparison.Ordinal);
        Assert.Contains("'Probe'", error.Message, StringComparison.Ordinal);
    }

    static ProcessorFailureException Diagnose(Exception thrown) =>
        new(Identity, ProjectionCheckpoint.Start, ProcessorFailureStage.Handling, innerException: thrown);

    internal sealed class ProbeRepository(CassieProjectionDatabase database, string componentName)
        : CassieProjectionRepository(database, componentName)
    {
        public void Write() => CreateCommand("SELECT 1").Dispose();
    }

    internal sealed partial class ProbeProjector(ProbeRepository repository)
        : BatchProjector(repository, EventStreamPattern.ForPattern("misuse", "accounts"), "Probe"),
            IProjectorHandler<ValueChanged>
    {
        public ValueTask<Result> HandleAsync(IProjectorContext<ValueChanged> context, CancellationToken ct) =>
            ValueTask.FromResult(Result.Success);
    }
}
