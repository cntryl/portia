namespace Cntryl.Portia;

/// <summary>Identifies an extensible request transport capability.</summary>
public readonly record struct RequestTransportId
{
    /// <summary>Creates a transport ID.</summary>
    public RequestTransportId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw new ArgumentException("A transport ID may contain only ASCII letters, digits, '.', '-', and '_'.",
                nameof(value));
        Value = value;
    }

    /// <summary>Gets the stable identifier.</summary>
    public string Value { get; }

    /// <summary>Gets the built-in callable capability.</summary>
    public static RequestTransportId Callable => new("callable");

    /// <summary>Gets the built-in durable queue capability.</summary>
    public static RequestTransportId Queue => new("queue");

    /// <summary>Gets the built-in notification capability.</summary>
    public static RequestTransportId Notice => new("notice");

    /// <summary>Gets the built-in scheduling capability.</summary>
    public static RequestTransportId Schedule => new("schedule");

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}
