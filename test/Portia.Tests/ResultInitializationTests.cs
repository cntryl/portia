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
    public void ShouldThrowForEveryMemberOfAnUninitializedResult(string member)
    {
        var result = default(Result);

        var error = Assert.Throws<InvalidOperationException>(() => _ = member == "IsSuccess"
            ? result.IsSuccess
            : result.Error is not null);

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
    public void ShouldThrowForEveryMemberOfAnUninitializedResultWithValue(string member)
    {
        var result = default(Result<int>);

        var error = Assert.Throws<InvalidOperationException>(() => _ = member switch
        {
            "IsSuccess" => result.IsSuccess,
            "Value" => result.Value != 0,
            _ => result.Error is not null
        });

        Assert.Equal("The result is uninitialized.", error.Message);
    }

    /// <summary>
    ///     Verifies that reading the value of a failed result is refused with its own message — the
    ///     result is initialized, so "uninitialized" would send the caller looking for a missing return
    ///     rather than at the failure branch they forgot to check.
    /// </summary>
    [Fact]
    public void ShouldDistinguishAFailedResultFromAnUninitializedOne()
    {
        var result = Result<int>.Failure(new RequestError(RequestErrorKind.Validation, "no"));

        var error = Assert.Throws<InvalidOperationException>(() => _ = result.Value);

        Assert.Equal("A failed result has no value.", error.Message);
    }

    /// <summary>
    ///     Verifies the positive control: results produced through the public factories answer normally,
    ///     including a success that legitimately carries null for a nullable type argument.
    /// </summary>
    [Fact]
    public void ShouldAnswerNormallyForResultsBuiltThroughTheFactories()
    {
        Assert.True(Result.Success.IsSuccess);
        Assert.Null(Result.Success.Error);
        Assert.Equal(7, Result<int>.Success(7).Value);
        Assert.Null(Result<string?>.Success(null).Value);
        Assert.False(Result.Failure(new RequestError(RequestErrorKind.Validation, "no")).IsSuccess);
    }

    /// <summary>Verifies that a failure cannot be built without describing itself.</summary>
    [Fact]
    public void ShouldRejectAFailureWithNoError()
    {
        _ = Assert.Throws<ArgumentNullException>(() => Result.Failure(null!));
        _ = Assert.Throws<ArgumentNullException>(() => Result<int>.Failure(null!));
    }
}
