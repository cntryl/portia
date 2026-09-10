namespace Cntryl.Portia;

/// <summary>
///     Describes an expected request-handling failure.
/// </summary>
/// <param name="kind">The category of failure.</param>
/// <param name="message">The error message.</param>
/// <param name="isTransient">
///     Whether retrying the request might succeed. <see langword="false" />
///     means the request will fail identically every time and should not be retried.
/// </param>
public sealed class RequestError(RequestErrorKind kind, string message, bool isTransient = false)
{
    /// <summary>
    ///     Gets the category of failure.
    /// </summary>
    public RequestErrorKind Kind { get; } = kind;

    /// <summary>
    ///     Gets the error message.
    /// </summary>
    public string Message { get; } = string.IsNullOrWhiteSpace(message)
        ? throw new ArgumentException("An error message cannot be empty.", nameof(message))
        : message;

    /// <summary>
    ///     Gets whether retrying the request might succeed.
    /// </summary>
    public bool IsTransient { get; } = isTransient;
}
