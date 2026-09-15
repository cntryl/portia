using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

/// <summary>
///     Verifies the read and write aggregate capabilities: components that must not persist depend on
///     <see cref="IAggregateReader" />, and every capability resolves to the one scoped repository.
/// </summary>
public sealed class AggregateRepositoryRegistrationTests
{
    /// <summary>The reader, writer, and repository are one scoped instance.</summary>
    [Fact]
    public async Task ShouldResolveReaderAndWriterAsSameScopedRepositoryInstance()
    {
        await using var provider = Provider(services => services.AddPortia());
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();

        var repository = first.ServiceProvider.GetRequiredService<IAggregateRepository>();

        Assert.Same(repository, first.ServiceProvider.GetRequiredService<IAggregateReader>());
        Assert.Same(repository, first.ServiceProvider.GetRequiredService<IAggregateWriter>());
        Assert.NotSame(repository, second.ServiceProvider.GetRequiredService<IAggregateReader>());
    }

    /// <summary>A replaced repository is what the reader and writer resolve to.</summary>
    [Fact]
    public async Task ShouldForwardReaderAndWriterToReplacedRepository()
    {
        var replacement = new RecordingAggregateRepository();
        await using var provider = Provider(services =>
        {
            _ = services.AddSingleton<IAggregateRepository>(replacement);
            _ = services.AddPortia();
        });
        await using var scope = provider.CreateAsyncScope();

        Assert.Same(replacement, scope.ServiceProvider.GetRequiredService<IAggregateReader>());
        Assert.Same(replacement, scope.ServiceProvider.GetRequiredService<IAggregateWriter>());
    }

    /// <summary>An application-registered reader is kept.</summary>
    [Fact]
    public async Task ShouldPreserveApplicationRegisteredReader()
    {
        var reader = new RecordingAggregateRepository();
        await using var provider = Provider(services =>
        {
            _ = services.AddSingleton<IAggregateReader>(reader);
            _ = services.AddPortia();
        });
        await using var scope = provider.CreateAsyncScope();

        Assert.Same(reader, scope.ServiceProvider.GetRequiredService<IAggregateReader>());
    }

    /// <summary>State saved through the writer hydrates through the reader.</summary>
    [Fact]
    public async Task ShouldHydrateThroughReaderAndSaveThroughWriter()
    {
        await using var provider = Provider(services => services.AddPortia());
        var id = Uuid.CreateVersion4();
        await using (var scope = provider.CreateAsyncScope())
        {
            var aggregate = new TestAggregate(id);
            aggregate.ChangeValue(7);
            await scope.ServiceProvider.GetRequiredService<IAggregateWriter>()
                .SaveAsync(aggregate, new RequestDispatchContext(RequestActor.System));
        }

        await using var reading = provider.CreateAsyncScope();
        var hydrated = await reading.ServiceProvider.GetRequiredService<IAggregateReader>()
            .HydrateAsync(new TestAggregate(id));

        Assert.Equal(7, hydrated.Value);
    }

    static ServiceProvider Provider(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        _ = services.AddSingleton<IEventStore>(new InMemoryEventStore());
        configure(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
