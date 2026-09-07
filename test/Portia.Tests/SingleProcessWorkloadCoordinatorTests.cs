using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

/// <summary>
/// Verifies the coordinator that lets projectors and reactors run with no coordination
/// infrastructure at all. Before it existed, <c>AddProjector</c>/<c>AddReactor</c> plus
/// <c>AddWorker()</c> could not start a single workload unless Fitz supplied an
/// <see cref="IWorkloadCoordinator" />, which is why the low-level hosting extensions existed.
/// </summary>
public sealed class SingleProcessWorkloadCoordinatorTests
{
    /// <summary>Owns and runs a workload present in the opening snapshot.</summary>
    [Fact]
    public async Task ShouldRunWorkloadDeclaredBeforeStart()
    {
        var coordinator = new SingleProcessWorkloadCoordinator(TimeSpan.FromMilliseconds(10));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new CancellationTokenSource();

        var run = coordinator.RunAsync(
            () => [new WorkloadIdentity("only")],
            async (_, _, ct) =>
            {
                _ = started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            },
            lifetime.Token);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await lifetime.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    /// <summary>
    /// A per-tenant workload only appears once its tenant does, so ownership has to be reconciled
    /// after start rather than captured once from the opening snapshot.
    /// </summary>
    [Fact]
    public async Task ShouldStartWorkloadThatAppearsAfterStartAndCancelOneThatDisappears()
    {
        var coordinator = new SingleProcessWorkloadCoordinator(TimeSpan.FromMilliseconds(10));
        var declared = new ConcurrentDictionary<WorkloadIdentity, bool> { [new WorkloadIdentity("first")] = true };
        var running = new ConcurrentDictionary<string, bool>();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = new CancellationTokenSource();

        var run = coordinator.RunAsync(
            () => [.. declared.Keys],
            async (identity, _fencingToken, ct) =>
            {
                running[identity.Name] = true;
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                finally
                {
                    running[identity.Name] = false;
                    if (identity.Name == "second")
                        _ = cancelled.TrySetResult();
                }
            },
            lifetime.Token);

        await WaitUntilAsync(() => running.GetValueOrDefault("first"));

        // A tenant arrives after the coordinator is already running.
        var second = new WorkloadIdentity("second", new TenantId("acme"));
        declared[second] = true;
        await WaitUntilAsync(() => running.GetValueOrDefault("second"));

        // ...and then goes away. Its callback must be cancelled and awaited.
        _ = declared.TryRemove(second, out _);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(running["second"]);
        Assert.True(running["first"]);

        await lifetime.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await WaitUntilAsync(() => !running["first"]);
    }

    /// <summary>Gives every owned workload its own non-zero fencing token.</summary>
    [Fact]
    public async Task ShouldIssueDistinctFencingTokensPerOwnedWorkload()
    {
        var coordinator = new SingleProcessWorkloadCoordinator(TimeSpan.FromMilliseconds(10));
        var tokens = new ConcurrentDictionary<string, ulong>();
        using var lifetime = new CancellationTokenSource();

        var run = coordinator.RunAsync(
            () => [new WorkloadIdentity("first"), new WorkloadIdentity("second")],
            async (identity, fencingToken, ct) =>
            {
                tokens[identity.Name] = fencingToken;
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            },
            lifetime.Token);

        await WaitUntilAsync(() => tokens.Count == 2);
        await lifetime.CancelAsync();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.All(tokens.Values, token => Assert.NotEqual(0UL, token));
        Assert.Equal(2, tokens.Values.Distinct().Count());
    }

    /// <summary>
    /// Regression test: a projector's or reactor's own <c>Name</c> is its checkpoint identity. A
    /// workload registration that does not set <c>WorkloadOptions.Name</c> must leave it alone —
    /// renaming it to the component's type name would silently repoint an existing deployment's
    /// checkpoints and replay the whole stream.
    /// </summary>
    [Theory]
    [InlineData(null, "declared-projection-name")]
    [InlineData("chosen-by-the-host", "chosen-by-the-host")]
    public async Task ShouldPreserveComponentCheckpointNameUnlessWorkloadNamesItExplicitly(string? workloadName, string expected)
    {
        var id = Uuid.CreateVersion4();
        var store = new InMemoryEventStore();
        await store.AppendAsync(new EventStreamAddress("test", "projectors", id.ToString()),
            0, [Committed(new ValueChanged(7), id, 1)]);
        var target = new RecordingProjectionTarget();

        var services = new ServiceCollection();
        _ = services.AddSingleton<IDomainEventReader>(store);
        _ = services.AddFrameworkTests();
        _ = services.AddSingleton(new NamedProjector(target));
        _ = services.AddPortia(p => p.AddProjector<NamedProjector>(o =>
        {
            o.Global();
            o.Name = workloadName;
            o.PollInterval = TimeSpan.FromMilliseconds(10);
        })).AddWorker();
        using var provider = services.BuildServiceProvider();

        var worker = Assert.Single(provider.GetServices<IHostedService>());
        await worker.StartAsync(default);
        await WaitUntilAsync(() => target.Projection.Value == 7);
        await worker.StopAsync(default);

        var name = Assert.Single(target.Contexts.Select(context => context.Identity.ComponentName).Distinct());
        Assert.Equal(expected, name);
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition());
    }

    static T Committed<T>(T ev, Uuid aggregateId, ulong aggregateVersion)
        where T : DomainEvent
    {
        ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), aggregateId, aggregateVersion, DateTimeOffset.UtcNow));
        return ev;
    }
}

sealed partial class NamedProjector(RecordingProjectionTarget target)
    : BaseProjector(target, EventStreamPattern.ForPattern("test", "projectors"), "declared-projection-name"),
        IProjectorHandler<ValueChanged>
{
    public ValueTask HandleAsync(ValueChanged ev, IProjectorContext context, CancellationToken ct)
    {
        target.Projection.Value = ev.Value;
        return ValueTask.CompletedTask;
    }
}
