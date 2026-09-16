namespace Cntryl.Portia.Consumer;

/// <summary>
///     Verifies the public aggregate surface: applications depend on the read and write capabilities and on
///     the executor that coordinates them, never on a combined repository abstraction.
/// </summary>
public sealed class AggregateCapabilitySurfaceTests
{
    [Fact]
    public void ShouldExposeOnlyReaderWriterAndExecutor()
    {
        var abstractions = typeof(IAggregateReader).Assembly;

        Assert.True(typeof(IAggregateReader).IsPublic);
        Assert.True(typeof(IAggregateWriter).IsPublic);
        Assert.True(typeof(IAggregateExecutor).IsPublic);
        Assert.Null(abstractions.GetType("Cntryl.Portia.IAggregateRepository", false));
        Assert.DoesNotContain(typeof(RequestBus).Assembly.GetExportedTypes(),
            type => type.Name.Contains("AggregateRepository", StringComparison.Ordinal));
    }
}
