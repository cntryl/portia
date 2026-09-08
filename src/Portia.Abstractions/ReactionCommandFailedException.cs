namespace Cntryl.Portia;

/// <summary>
/// Indicates that a command dispatched as a reaction returned an expected application failure.
/// Throwing prevents the reactor checkpoint from advancing so the event remains eligible for
/// replay. The exception message deliberately excludes the application error message.
/// </summary>
/// <param name="error">The expected request failure returned by the command.</param>
public sealed class ReactionCommandFailedException(RequestError error)
    : Exception("A command dispatched by a reactor did not succeed.")
{
    /// <summary>Gets the expected request failure returned by the command.</summary>
    public RequestError Error { get; } = error ?? throw new ArgumentNullException(nameof(error));
}
