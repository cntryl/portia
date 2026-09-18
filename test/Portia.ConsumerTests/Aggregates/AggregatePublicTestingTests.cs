namespace Cntryl.Portia.Consumer;

public sealed class AggregatePublicTestingTests
{
    [Fact]
    public void MetadataAndBusinessTestHelpersRequireNoInternalAccess()
    {
        var assembly = GeneratorCompilation.Compile("""
                                                    using System;
                                                    using Cntryl.Portia;
                                                    using Cntryl.Portia.Testing;
                                                    using Cntryl.Portia.Consumer;
                                                    public static class Scenario
                                                    {
                                                        public static int Run()
                                                        {
                                                            var id = Uuid.CreateVersion4();
                                                            var ev = new Deposited(10);
                                                            var metadata = new DomainEventMetadata(Uuid.CreateVersion4(), id, 1, DateTimeOffset.UtcNow);
                                                            ev.AttachMetadata(metadata);
                                                            try { ev.AttachMetadata(metadata); return -1; }
                                                            catch (InvalidOperationException) { }
                                                            var scenario = new AggregateScenario<Account>(new Account(id));
                                                            scenario.Given(ev, DomainEventSeed.Attach(new Deposited(5), id, 2));
                                                            scenario.Aggregate.Deposit(3);
                                                            return scenario.Aggregate.Balance + scenario.PendingEvents.Count + (int)scenario.CommittedEventCount;
                                                        }
                                                    }
                                                    """);
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<int>>();
        Assert.Equal(21, run());
    }
}
