namespace Cntryl.Portia.Consumer;

public sealed class ReactorExecutionContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedReactionSavesAsSystemAndUsesTriggeringEventAsCause(bool fitz)
    {
        var assembly = GeneratorCompilation.Compile("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Cntryl.Portia.Consumer;
            using Microsoft.Extensions.DependencyInjection;
            public sealed partial class Reaction(IAggregateRepository repository, Account account, Uuid sourceId)
                : BaseReactor(new InMemoryProjectionCheckpointStore(), EventStreamPattern.ForPattern("reaction", "inputs", sourceId.ToString()), "reaction"), IReactorHandler<Deposited>
            {
                public async ValueTask HandleAsync(IReactorContext<Deposited> context, CancellationToken ct)
                {
                    if (!RequestActor.IsSystem(context.Actor) || context.Source.Stream.Area != "inputs")
                        throw new Exception("Reaction context lost system authority or source");
                    account.Deposit(context.Ev.Amount);
                    await repository.SaveAsync(account, context, ct);
                }
            }
            public static class Scenario
            {
                public static async Task Run(IEventStore store)
                {
                    var id = Uuid.CreateVersion4();
                    var correlation = Uuid.CreateVersion4();
                    var ev = new Deposited(10);
                    ev.AttachMetadata(new DomainEventMetadata(Uuid.CreateVersion4(), id, 1, DateTimeOffset.UtcNow, correlation) { Actor = new ActorAttribution("alice", "users"), ExecutionId = Uuid.CreateVersion4() });
                    await store.AppendAsync(new EventStreamAddress("reaction", "inputs", id.ToString()), 0, new DomainEvent[] {ev});
                    var services = new ServiceCollection();
                    services.AddSingleton<IEventStore>(store);
                    services.AddPortia();
                    await using var provider = services.BuildServiceProvider();
                    await using var scope = provider.CreateAsyncScope();
                    var account = new Account(Uuid.CreateVersion4());
                    var reaction = new Reaction(scope.ServiceProvider.GetRequiredService<IAggregateRepository>(), account, id);
                    await new ReactorRunner(store).RunAsync(reaction, ProjectionCheckpoint.Start);
                    await foreach(var output in store.ReadAsync(account.Stream))
                        if (output.Ev.Metadata.CausationId != ev.Metadata.EventId || output.Ev.Metadata.CorrelationId != correlation
                            || output.Ev.Metadata.Actor?.Subject != "portia:system") throw new Exception("Incorrect reaction attribution");
                }
            }
            """, new ProjectorReactorEventDispatcherGenerator());
        await using var fixture = await StoreFixture.CreateAsync(fitz);
        await assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<IEventStore, Task>>()(fixture.Store);
    }
}
