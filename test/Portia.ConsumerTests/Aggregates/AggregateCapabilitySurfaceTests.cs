namespace Cntryl.Portia.Consumer;

/// <summary>
///     Verifies the public aggregate surface: applications depend on the read and write capabilities and on
///     the executor that coordinates them, never on a combined repository abstraction.
/// </summary>
public sealed class AggregateCapabilitySurfaceTests
{
    [Fact]
    public void ShouldExposeOnlyReaderWriterAndExecutorGivenAbstractionsAssembly()
    {
        // Arrange
        var abstractions = typeof(IAggregateReader).Assembly;

        // Act
        var combinedRepository = abstractions.GetType("Cntryl.Portia.IAggregateRepository", false);
        var exportedTypes = typeof(RequestBus).Assembly.GetExportedTypes();

        // Assert
        Assert.True(typeof(IAggregateReader).IsPublic);
        Assert.True(typeof(IAggregateWriter).IsPublic);
        Assert.True(typeof(IAggregateExecutor).IsPublic);
        Assert.Null(combinedRepository);
        Assert.DoesNotContain(exportedTypes,
            type => type.Name.Contains("AggregateRepository", StringComparison.Ordinal));
    }
}
