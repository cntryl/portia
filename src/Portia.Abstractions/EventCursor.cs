namespace Cntryl.Portia;

/// <summary>
///     An opaque position returned by an event reader and supplied unchanged when resuming a pattern read.
/// </summary>
public readonly record struct EventCursor
{
    /// <summary>Creates a cursor from a backend-owned token.</summary>
    /// <param name="value">The non-empty backend-owned token.</param>
    public EventCursor(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    /// <summary>Gets the backend-owned token. Applications must not interpret this value.</summary>
    public string? Value { get; }

    /// <summary>Gets the cursor used to begin a new read.</summary>
    public static EventCursor Start => default;

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}
