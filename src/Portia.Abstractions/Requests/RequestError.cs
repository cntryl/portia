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

    /// <summary>Creates a <see cref="RequestErrorKind.Validation" /> error: the input was rejected.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="isTransient">Whether retrying the request might succeed.</param>
    /// <returns>The error.</returns>
    public static RequestError Validation(string message, bool isTransient = false) =>
        new(RequestErrorKind.Validation, message, isTransient);

    /// <summary>Creates a <see cref="RequestErrorKind.Unauthorized" /> error: the actor is not authenticated.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="isTransient">Whether retrying the request might succeed.</param>
    /// <returns>The error.</returns>
    public static RequestError Unauthorized(string message, bool isTransient = false) =>
        new(RequestErrorKind.Unauthorized, message, isTransient);

    /// <summary>Creates a <see cref="RequestErrorKind.Forbidden" /> error: the actor may not do this.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="isTransient">Whether retrying the request might succeed.</param>
    /// <returns>The error.</returns>
    public static RequestError Forbidden(string message, bool isTransient = false) =>
        new(RequestErrorKind.Forbidden, message, isTransient);

    /// <summary>Creates a <see cref="RequestErrorKind.NotFound" /> error: the target does not exist.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="isTransient">Whether retrying the request might succeed.</param>
    /// <returns>The error.</returns>
    public static RequestError NotFound(string message, bool isTransient = false) =>
        new(RequestErrorKind.NotFound, message, isTransient);

    /// <summary>Creates a <see cref="RequestErrorKind.Conflict" /> error: current state refuses the operation.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="isTransient">Whether retrying the request might succeed.</param>
    /// <returns>The error.</returns>
    public static RequestError Conflict(string message, bool isTransient = false) =>
        new(RequestErrorKind.Conflict, message, isTransient);
}
