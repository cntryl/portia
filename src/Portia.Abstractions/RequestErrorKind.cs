namespace Cntryl.Portia;

/// <summary>
///     Categorizes an expected request-handling failure, so every transport (HTTP, RPC, a queue's
///     retry-or-drop decision) can map it to its own error semantics the same way.
/// </summary>
public enum RequestErrorKind
{
    /// <summary>
    ///     The request's input was invalid.
    /// </summary>
    Validation,

    /// <summary>
    ///     The caller was not authenticated.
    /// </summary>
    Unauthorized,

    /// <summary>
    ///     The caller was authenticated but not authorized.
    /// </summary>
    Forbidden,

    /// <summary>
    ///     Something the request referenced does not exist.
    /// </summary>
    NotFound,

    /// <summary>
    ///     The request conflicted with the current state of what it targeted.
    /// </summary>
    Conflict
}
