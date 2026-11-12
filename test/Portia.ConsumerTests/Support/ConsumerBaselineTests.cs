namespace Cntryl.Portia.Consumer;

public sealed class ConsumerBaselineTests
{
    [Fact]
    public void PublicAggregateAppliesContractFromSeparateAssembly()
    {
        // Arrange
        var account = new Account(Uuid.CreateVersion4());

        // Act
        account.Deposit(12);

        // Assert
        Assert.Equal(12, account.Balance);
        Assert.Equal(1UL, account.Version);
        Assert.NotEqual(typeof(Deposited).Assembly, typeof(Account).Assembly);
        Assert.NotEqual(typeof(AccountsRegistration).Assembly, typeof(ReportingRegistration).Assembly);
    }

    [Fact]
    public void GeneratorHarnessCompilesAndExecutesConsumer()
    {
        // Arrange
        var assembly = GeneratorCompilation.Compile("""
                                                    public static class Example { public static int Run() => 42; }
                                                    """);

        // Act
        var run = assembly.GetType("Example")!.GetMethod("Run")!.CreateDelegate<Func<int>>();

        // Assert
        Assert.Equal(42, run());
    }
}
