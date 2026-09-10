using System.Diagnostics.CodeAnalysis;

namespace Cntryl.Portia;

/// <summary>
///     The outcome of handling a no-result request: success, or an expected failure. An
///     unrecognized (unexpected, infrastructure-level) failure is a plain exception, not a
///     <see cref="Result" /> — this type exists for failures a handler anticipates as part of its
///     normal contract.
///     <para>
///         <c>default(Result)</c> is deliberately not a valid value: it is neither success nor a
///         described failure, and every member throws rather than guess which. Silently treating a
///         forgotten <c>return</c> as success would be unsafe, and treating it as an undescribed failure
///         would hide the bug — so the framework fails loudly instead, and names the handler, pipeline
///         behavior, or authorizer responsible when an uninitialized result crosses its boundary.
///         Always produce one through <see cref="Success" /> or <see cref="Failure" />.
///     </para>
/// </summary>
public readonly struct Result
{
    readonly ResultState _state;
    RequestError? StoredError { get; }

    Result(ResultState state, RequestError? error)
    {
        _state = state;
        StoredError = error;
    }

    /// <summary>
    ///     Gets a successful result.
    /// </summary>
    public static Result Success { get; } = new(ResultState.Succeeded, null);

    /// <summary>
    ///     Gets whether the request succeeded. When <see langword="false" />,
    ///     <see cref="Error" /> is non-null, so a failure branch needs no null check.
    /// </summary>
    [MemberNotNullWhen(false, nameof(Error))]
    [MemberNotNullWhen(false, nameof(StoredError))]
    public bool IsSuccess => _state switch
    {
        ResultState.Succeeded => true,
        ResultState.Failed => false,
        ResultState.Uninitialized or _ => throw new InvalidOperationException("The result is uninitialized.")
    };

    /// <summary>
    ///     Gets the error, when <see cref="IsSuccess" /> is <see langword="false" />.
    /// </summary>
    public RequestError? Error => _state switch
    {
        ResultState.Succeeded => null,
        ResultState.Failed => StoredError,
        ResultState.Uninitialized or _ => throw new InvalidOperationException("The result is uninitialized.")
    };

    /// <summary>
    ///     Creates a failed result.
    /// </summary>
    /// <param name="error">The failure.</param>
    public static Result Failure(RequestError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(ResultState.Failed, error);
    }
}

/// <summary>
///     The outcome of handling a request that produces a result: success with a value, or an
///     expected failure. An unrecognized (unexpected, infrastructure-level) failure is a plain
///     exception, not a <see cref="Result{T}" /> — this type exists for failures a handler
///     anticipates as part of its normal contract.
///     <para>
///         <c>default(Result{T})</c> is deliberately not a valid value, for the reasons given on
///         <see cref="Result" />. Always produce one through <see cref="Success" /> or
///         <see cref="Failure" />.
///     </para>
/// </summary>
/// <typeparam name="T">The type of the value produced on success.</typeparam>
public readonly struct Result<T>
{
    readonly ResultState _state;
    T? StoredValue { get; }
    RequestError? StoredError { get; }

    Result(ResultState state, T? value, RequestError? error)
    {
        _state = state;
        StoredValue = value;
        StoredError = error;
    }

    /// <summary>
    ///     Gets whether the request succeeded. When <see langword="false" />,
    ///     <see cref="Error" /> is non-null, so a failure branch needs no null check. On success,
    ///     <see cref="Value" /> has exactly the nullability declared by <typeparamref name="T" />:
    ///     <c>Result&lt;string&gt;</c> exposes <c>string</c>, while
    ///     <c>Result&lt;string?&gt;.Success(null)</c> remains a legitimate success carrying null.
    /// </summary>
    [MemberNotNullWhen(false, nameof(Error))]
    [MemberNotNullWhen(false, nameof(StoredError))]
    public bool IsSuccess => _state switch
    {
        ResultState.Succeeded => true,
        ResultState.Failed => false,
        ResultState.Uninitialized or _ => throw new InvalidOperationException("The result is uninitialized.")
    };

    /// <summary>
    ///     Gets the value, when <see cref="IsSuccess" /> is <see langword="true" />.
    /// </summary>
    public T Value => _state switch
    {
        ResultState.Succeeded => StoredValue!,
        ResultState.Failed => throw new InvalidOperationException("A failed result has no value."),
        ResultState.Uninitialized or _ => throw new InvalidOperationException("The result is uninitialized.")
    };

    /// <summary>
    ///     Gets the error, when <see cref="IsSuccess" /> is <see langword="false" />.
    /// </summary>
    public RequestError? Error => _state switch
    {
        ResultState.Succeeded => null,
        ResultState.Failed => StoredError,
        ResultState.Uninitialized or _ => throw new InvalidOperationException("The result is uninitialized.")
    };

    // CA1000 (no static members on generic types) is the wrong call for a Result<T> factory —
    // Result<T>.Success(value)/.Failure(error) at the call site is exactly the point of the
    // pattern (mirrors ErrorOr/FluentResults, which suppress the same rule for the same reason).
#pragma warning disable CA1000
    /// <summary>
    ///     Creates a successful result.
    /// </summary>
    /// <param name="value">The value produced.</param>
    public static Result<T> Success(T value) => new(ResultState.Succeeded, value, null);

    /// <summary>
    ///     Creates a failed result.
    /// </summary>
    /// <param name="error">The failure.</param>
    public static Result<T> Failure(RequestError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<T>(ResultState.Failed, default, error);
    }
#pragma warning restore CA1000
}
