namespace Cntryl.Portia;

/// <summary>Checks Fitz's actual resource locking, rather than assuming a KV isolation model.</summary>
[Collection(FitzBrokerCollectionDefinition.Name)]
[Trait("Category", "BrokerIntegration")]
public sealed class OperationalConcurrencyIntegrationTests(FitzBrokerFixture broker)
{
    /// <summary>A competing writer is rejected at BEGIN and cannot overwrite the current revision.</summary>
    [Fact]
    public async Task ShouldFenceConcurrentOperationalWritersUsingRealFitzLocks()
    {
        await using var client = await broker.CreateClientAsync();
        var realm = "operations-concurrency-" + Uuid.CreateVersion4();
        var target = new OperationalTarget("reactor", $"stream://{realm}/events/*", OperationalScope.Global);
        var route = $"kv://{realm}/portia/checkpoints";
        var journal = new FitzOperationalStore(client.Kv, route);
        var state = new OperationalProcessorState(target, "reactor", Guid.NewGuid(), 1, null,
            new OperationalProcessingSettings(1, 1, 3, 1, 1), [], null, [], [], [], 0, DateTimeOffset.UtcNow);
        Assert.True(await journal.CompareExchangeAsync(state, 0, Guid.Empty));
        var next = state with { Revision = 2, ConsecutiveFailures = 1 };
        await using (var competing = await client.Kv.BeginAsync(FitzKvCheckpoints.WorkloadRoute(route, target.Identity)))
        {
            _ = await Assert.ThrowsAsync<ProjectionConcurrencyException>(() => journal
                .CompareExchangeAsync(next, state.Revision, state.OwnerSession).AsTask());
            Assert.Equal(1, (await journal.ReadAsync(target))!.Revision);
            await competing.RollbackAsync();
        }
        Assert.True(await journal.CompareExchangeAsync(next, state.Revision, state.OwnerSession));
        Assert.False(await journal.CompareExchangeAsync(next, state.Revision, state.OwnerSession));
        Assert.Equal(2, (await journal.ReadAsync(target))!.Revision);
    }
}
