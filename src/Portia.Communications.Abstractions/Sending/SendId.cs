namespace Cntryl.Portia.Communications;

/// <summary>The application's identity for one outbound message, used to correlate provider delivery events.</summary>
/// <remarks>
///     Use the identity the application's aggregate records for this send, so provider delivery events
///     can be routed back to it. It travels to providers as a correlation tag and callback path segment,
///     so it must not contain personal data. It is 1–128 ASCII letters, digits, '.', '_' or '-'.
/// </remarks>
public readonly record struct SendId
{
    /// <summary>The longest accepted identity.</summary>
    public const int MaximumLength = 128;

    /// <summary>Creates a send identity.</summary>
    /// <param name="value">The identity text.</param>
    /// <exception cref="ArgumentException">The text is empty, too long, or contains other characters.</exception>
    public SendId(string value)
    {
        Value = IsValid(value)
            ? value
            : throw new ArgumentException(
                $"A send ID must be 1-{MaximumLength} ASCII letters, digits, '.', '_' or '-'.", nameof(value));
    }

    /// <summary>Gets the identity text.</summary>
    public string Value { get; }

    /// <summary>Checks whether text is a valid send identity.</summary>
    /// <param name="value">The text to check.</param>
    /// <returns><see langword="true" /> when the text can be used as a <see cref="SendId" />.</returns>
    public static bool IsValid([NotNullWhen(true)] string? value) =>
        value is { Length: > 0 and <= MaximumLength } && value.All(IsIdentityCharacter);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;

    internal static SendId Require(SendId id, string parameterName) => id.Value is null
        ? throw new ArgumentException("A send ID is required.", parameterName)
        : id;

    static bool IsIdentityCharacter(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-';
}
