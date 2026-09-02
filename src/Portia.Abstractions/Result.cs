namespace Cntryl.Portia;

/// <summary>
/// The outcome of handling a no-result request: success, or an expected failure. An
/// unrecognized (unexpected, infrastructure-level) failure is a plain exception, not a
/// <see cref="Result" /> — this type exists for failures a handler anticipates as part of its
/// normal contract.
/// </summary>
public readonly struct Result
{
    Result(bool isSuccess, RequestError? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    /// <summary>
    /// Gets a successful result.
    /// </summary>
    public static Result Success { get; } = new(true, null);

    /// <summary>
    /// Gets whether the request succeeded.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Gets the error, when <see cref="IsSuccess" /> is <see langword="false" />.
    /// </summary>
    public RequestError? Error { get; }

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    /// <param name="error">The failure.</param>
    public static Result Failure(RequestError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(false, error);
    }
}

/// <summary>
/// The outcome of handling a request that produces a result: success with a value, or an
/// expected failure. An unrecognized (unexpected, infrastructure-level) failure is a plain
/// exception, not a <see cref="Result{T}" /> — this type exists for failures a handler
/// anticipates as part of its normal contract.
/// </summary>
/// <typeparam name="T">The type of the value produced on success.</typeparam>
public readonly struct Result<T>
{
    Result(bool isSuccess, T? value, RequestError? error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    /// <summary>
    /// Gets whether the request succeeded.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Gets the value, when <see cref="IsSuccess" /> is <see langword="true" />.
    /// </summary>
    public T? Value { get; }

    /// <summary>
    /// Gets the error, when <see cref="IsSuccess" /> is <see langword="false" />.
    /// </summary>
    public RequestError? Error { get; }

    // CA1000 (no static members on generic types) is the wrong call for a Result<T> factory —
    // Result<T>.Success(value)/.Failure(error) at the call site is exactly the point of the
    // pattern (mirrors ErrorOr/FluentResults, which suppress the same rule for the same reason).
#pragma warning disable CA1000
    /// <summary>
    /// Creates a successful result.
    /// </summary>
    /// <param name="value">The value produced.</param>
    public static Result<T> Success(T value) => new(true, value, null);

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    /// <param name="error">The failure.</param>
    public static Result<T> Failure(RequestError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(false, default, error);
    }
#pragma warning restore CA1000
}
