namespace Cntryl.Portia;

/// <summary>
///     Verifies that <c>default(Result)</c> is never mistaken for a value. A handler that falls off the
///     end of a branch returns exactly that, and the framework's whole position is that guessing —
///     success, or an undescribed failure — would either be unsafe or hide the bug. Every member fails
///     loudly instead, and this covers the primitive itself rather than the pipeline that names its
///     owner.
/// </summary>
public sealed class ResultInitializationTests
{
    /// <summary>
    ///     Verifies that every member of an uninitialized no-result outcome refuses to answer, including
    ///     the success flag a caller would branch on first.
    /// </summary>
    /// <param name="member">The member read from the uninitialized result.</param>
    [Theory]
    [InlineData("IsSuccess")]
    [InlineData("Error")]
    public void ShouldThrowForEveryMemberGivenUninitializedResult(string member)
    {
        // Arrange
        var result = default(Result);

        // Act
        var error = Record.Exception(() => _ = member == "IsSuccess"
            ? result.IsSuccess
            : result.Error is not null);

        // Assert
        _ = Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("The result is uninitialized.", error.Message);
    }

    /// <summary>
    ///     Verifies the same for a result-bearing outcome, whose extra value member must fail the same
    ///     way rather than hand back the type's default.
    /// </summary>
    /// <param name="member">The member read from the uninitialized result.</param>
    [Theory]
    [InlineData("IsSuccess")]
    [InlineData("Value")]
    [InlineData("Error")]
    public void ShouldThrowForEveryMemberGivenUninitializedResultWithValue(string member)
    {
        // Arrange
        var result = default(Result<int>);

        // Act
        var error = Record.Exception(() => _ = member switch
        {
            "IsSuccess" => result.IsSuccess,
            "Value" => result.Value != 0,
            _ => result.Error is not null
        });

        // Assert
        _ = Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("The result is uninitialized.", error.Message);
    }

    /// <summary>
    ///     Verifies that reading the value of a failed result is refused with its own message — the
    ///     result is initialized, so "uninitialized" would send the caller looking for a missing return
    ///     rather than at the failure branch they forgot to check.
    /// </summary>
    [Fact]
    public void ShouldDistinguishFailedResultGivenUninitializedResult()
    {
        // Arrange
        var result = Result<int>.Failure(new RequestError(RequestErrorKind.Validation, "no"));

        // Act
        var error = Record.Exception(() => _ = result.Value);

        // Assert
        _ = Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("A failed result has no value.", error.Message);
    }

    /// <summary>
    ///     Verifies the positive control: results produced through the public factories answer normally,
    ///     including a success that legitimately carries null for a nullable type argument.
    /// </summary>
    [Fact]
    public void ShouldAnswerNormallyGivenResultsBuiltThroughFactories()
    {
        // Arrange
        var error = new RequestError(RequestErrorKind.Validation, "no");

        // Act
        var success = Result.Success;
        var valueSuccess = Result<int>.Success(7);
        var nullSuccess = Result<string?>.Success(null);
        var failure = Result.Failure(error);

        // Assert
        Assert.True(success.IsSuccess);
        Assert.Null(success.Error);
        Assert.Equal(7, valueSuccess.Value);
        Assert.Null(nullSuccess.Value);
        Assert.False(failure.IsSuccess);
    }

    /// <summary>Verifies that a failure cannot be built without describing itself.</summary>
    [Fact]
    public void ShouldRejectFailureWithNoErrorGivenNoResultOutcome()
    {
        // Arrange
        RequestError? error = null;

        // Act
        var exception = Record.Exception(() => Result.Failure(error!));

        // Assert
        _ = Assert.IsType<ArgumentNullException>(exception);
    }

    /// <summary>Verifies that a result-bearing failure cannot be built without describing itself.</summary>
    [Fact]
    public void ShouldRejectFailureWithNoErrorGivenResultWithValue()
    {
        // Arrange
        RequestError? error = null;

        // Act
        var exception = Record.Exception(() => Result<int>.Failure(error!));

        // Assert
        _ = Assert.IsType<ArgumentNullException>(exception);
    }
}
