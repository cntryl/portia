using Cntryl.Cassie;

namespace Cntryl.Portia;

/// <summary>Checks classification of errors raised while probing the Cassie checkpoint schema.</summary>
public sealed class CassieProjectionDatabaseTests
{
    /// <summary>Only schema-shape errors should be reported as an incompatible checkpoint table.</summary>
    /// <param name="sqlState">The Cassie SQLSTATE to classify.</param>
    /// <param name="expected">Whether it identifies a checkpoint schema mismatch.</param>
    [Theory]
    [InlineData("42601", true)]
    [InlineData("42703", true)]
    [InlineData("42P10", true)]
    [InlineData("22P02", true)]
    [InlineData("23502", true)]
    [InlineData("42804", true)]
    [InlineData("42846", true)]
    [InlineData("42501", false)]
    [InlineData("P0001", false)]
    [InlineData("40001", false)]
    [InlineData("22000", false)]
    [InlineData("23514", false)]
    public void ShouldClassifyCheckpointSchemaErrorsOnly(string sqlState, bool expected)
    {
        // Arrange
        var error = new CassieException("probe failed", sqlState);

        // Act
        var actual = CassieProjectionDatabase.IsCheckpointSchemaFailure(error);

        // Assert
        Assert.Equal(expected, actual);
    }
}
