namespace Cntryl.Portia;

/// <summary>
///     Verifies the short forms of an expected failure: a <see cref="RequestError" /> factory per kind, and
///     an implicit conversion from an error or a value to the matching <see cref="Result" />. They exist so a
///     rejection is one call rather than a nested constructor, and so an application never needs its own
///     <c>Failure</c> helper to say the same thing.
/// </summary>
public sealed class ResultConversionTests
{
    /// <summary>Each factory produces an error of its own kind and defaults to a permanent failure.</summary>
    /// <param name="kind">The kind the factory stands for.</param>
    [Theory]
    [InlineData(RequestErrorKind.Validation)]
    [InlineData(RequestErrorKind.Unauthorized)]
    [InlineData(RequestErrorKind.Forbidden)]
    [InlineData(RequestErrorKind.NotFound)]
    [InlineData(RequestErrorKind.Conflict)]
    public void ShouldCreateErrorOfRequestedKind(RequestErrorKind kind)
    {
        var error = Create(kind, "The operation was rejected.");

        Assert.Equal(kind, error.Kind);
        Assert.Equal("The operation was rejected.", error.Message);
        Assert.False(error.IsTransient);
    }

    /// <summary>A factory still carries the transient flag through to the error.</summary>
    [Fact]
    public void ShouldCarryTransientFlagThroughFactory()
    {
        var error = RequestError.Conflict("Operations are paused.", isTransient: true);

        Assert.True(error.IsTransient);
    }

    /// <summary>A factory applies the same message rule as the constructor.</summary>
    [Fact]
    public void ShouldRejectBlankMessageFromFactory() =>
        Assert.Throws<ArgumentException>(() => RequestError.Validation(" "));

    /// <summary>An error converts to a failed no-value result carrying that same error.</summary>
    [Fact]
    public void ShouldConvertErrorToFailedResult()
    {
        var error = RequestError.NotFound("The account is not open.");

        Result result = error;

        Assert.False(result.IsSuccess);
        Assert.Same(error, result.Error);
    }

    /// <summary>An error converts to a failed value result carrying that same error.</summary>
    [Fact]
    public void ShouldConvertErrorToFailedValueResult()
    {
        var error = RequestError.Forbidden("The actor cannot access this account.");

        Result<int> result = error;

        Assert.False(result.IsSuccess);
        Assert.Same(error, result.Error);
    }

    /// <summary>A value converts to a successful value result.</summary>
    [Fact]
    public void ShouldConvertValueToSuccessfulResult()
    {
        Result<string> result = "savings";

        Assert.True(result.IsSuccess);
        Assert.Equal("savings", result.Value);
    }

    /// <summary>A method returning a result can return either side without naming the result type.</summary>
    [Fact]
    public void ShouldReturnEitherSideFromOneMethod()
    {
        Assert.True(Open(amount: 5).IsSuccess);
        Assert.Equal(RequestErrorKind.Validation, Open(amount: -1).Error!.Kind);
    }

    static Result<int> Open(int amount) => amount < 0
        ? RequestError.Validation("The opening balance cannot be negative.")
        : amount;

    static RequestError Create(RequestErrorKind kind, string message) => kind switch
    {
        RequestErrorKind.Validation => RequestError.Validation(message),
        RequestErrorKind.Unauthorized => RequestError.Unauthorized(message),
        RequestErrorKind.Forbidden => RequestError.Forbidden(message),
        RequestErrorKind.NotFound => RequestError.NotFound(message),
        RequestErrorKind.Conflict => RequestError.Conflict(message),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
