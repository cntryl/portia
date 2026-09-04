namespace Cntryl.Portia.Consumer;

public sealed class ConsumerBaselineTests
{
    [Fact]
    public void PublicAggregateAppliesContractFromSeparateAssembly()
    {
        var account = new Account(Uuid.CreateVersion7());
        account.Deposit(12);
        Assert.Equal(12, account.Balance);
        Assert.Equal(1UL, account.Version);
        Assert.NotEqual(typeof(Deposited).Assembly, typeof(Account).Assembly);
        Assert.NotEqual(typeof(AccountsModule).Assembly, typeof(ReportingModule).Assembly);
    }

    [Fact]
    public void GeneratorHarnessCompilesAndExecutesConsumer()
    {
        var assembly = GeneratorCompilation.Compile("""
            public static class Example { public static int Run() => 42; }
            """, new RequestBusGenerator());
        var run = assembly.GetType("Example")!.GetMethod("Run")!.CreateDelegate<Func<int>>();
        Assert.Equal(42, run());
    }
}
