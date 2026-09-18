using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Verifies <see cref="AggregateCapabilities" /> and the <c>Aggregates()</c> extension combine the scope's
///     <see cref="IAggregateReader" />, <see cref="IAggregateWriter" />, and <see cref="IAggregateExecutor" />
///     into one double for tests that seed, execute against, or assert on aggregate state directly.
/// </summary>
public sealed class AggregateCapabilitiesTests
{
    /// <summary>The combined double exposes all three aggregate capabilities.</summary>
    [Fact]
    public async Task ShouldExposeReaderWriterAndExecutorCapabilities()
    {
        await using var provider = Provider();
        await using var scope = provider.CreateAsyncScope();

        var capabilities = scope.ServiceProvider.Aggregates();

        Assert.IsAssignableFrom<IAggregateReader>(capabilities);
        Assert.IsAssignableFrom<IAggregateWriter>(capabilities);
        Assert.IsAssignableFrom<IAggregateExecutor>(capabilities);
    }

    /// <summary>State committed through the executor hydrates back through the same double.</summary>
    [Fact]
    public async Task ShouldHydrateStateCommittedThroughExecutor()
    {
        await using var provider = Provider();
        var id = Uuid.CreateVersion4();

        await using (var scope = provider.CreateAsyncScope())
        {
            var capabilities = scope.ServiceProvider.Aggregates();
            await capabilities.ExecuteAsync(new TestAggregate(id), aggregate =>
            {
                aggregate.ChangeValue(5);
                return AggregateOutcome.Commit(Result.Success);
            }, new RequestDispatchContext(RequestActor.System));
        }

        await using var reading = provider.CreateAsyncScope();
        var hydrated = await reading.ServiceProvider.Aggregates().HydrateAsync(new TestAggregate(id));

        Assert.Equal(5, hydrated.Value);
    }

    /// <summary>State saved through the combined double hydrates back through the same double.</summary>
    [Fact]
    public async Task ShouldHydrateStateSavedThroughItself()
    {
        await using var provider = Provider();
        var id = Uuid.CreateVersion4();

        await using (var scope = provider.CreateAsyncScope())
        {
            var capabilities = scope.ServiceProvider.Aggregates();
            var aggregate = new TestAggregate(id);
            aggregate.ChangeValue(9);
            await capabilities.SaveAsync(aggregate, new RequestDispatchContext(RequestActor.System));
        }

        await using var reading = provider.CreateAsyncScope();
        var hydrated = await reading.ServiceProvider.Aggregates().HydrateAsync(new TestAggregate(id));

        Assert.Equal(9, hydrated.Value);
    }

    static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton<IEventStore>(new InMemoryEventStore());
        _ = services.AddPortia();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
