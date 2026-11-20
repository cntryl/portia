using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Cntryl.Portia;

/// <summary>The business decision established by a resource write.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PortiaCreateOutcomeKind>))]
public enum PortiaCreateOutcomeKind
{
    /// <summary>The call committed a new resource.</summary>
    Created = 1,

    /// <summary>The handler confirmed an identical earlier request.</summary>
    Repeated,

    /// <summary>The call committed a replacement of an existing resource.</summary>
    Replaced
}

interface IPortiaCreateOutcome
{
    object? UntypedValue { get; }
    JsonTypeInfo? PayloadTypeInfo(JsonSerializerOptions options);
    PortiaCreateOutcomeKind Kind { get; }
}

/// <summary>Represents a committed resource write or confirmed repeat without a payload.</summary>
public sealed class PortiaCreateOutcome : IPortiaCreateOutcome
{
    /// <summary>Reconstructs the outcome from its serialized outcome kind.</summary>
    [JsonConstructor]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PortiaCreateOutcome(PortiaCreateOutcomeKind kind) => Kind = ValidateKind(kind);

    internal static PortiaCreateOutcomeKind ValidateKind(PortiaCreateOutcomeKind kind) =>
        Enum.IsDefined(kind) ? kind : throw new JsonException("Invalid or missing resource write outcome kind.");

    /// <summary>The decision established by the handler.</summary>
    public PortiaCreateOutcomeKind Kind { get; }

    object? IPortiaCreateOutcome.UntypedValue => null;

    JsonTypeInfo? IPortiaCreateOutcome.PayloadTypeInfo(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return null;
    }

#pragma warning disable CA1000
    /// <summary>Creates a newly committed outcome with no response payload.</summary>
    public static PortiaCreateOutcome Created() => new(PortiaCreateOutcomeKind.Created);

    /// <summary>Creates an outcome after confirming an identical earlier request, with no response payload.</summary>
    public static PortiaCreateOutcome Repeated() => new(PortiaCreateOutcomeKind.Repeated);

    /// <summary>Creates a committed replacement outcome with no response payload.</summary>
    public static PortiaCreateOutcome Replaced() => new(PortiaCreateOutcomeKind.Replaced);
#pragma warning restore CA1000
}

/// <summary>Represents a committed resource write or confirmed repeat with a payload.</summary>
public sealed class PortiaCreateOutcome<T> : IPortiaCreateOutcome
{
    /// <summary>Reconstructs the outcome from its serialized value and outcome kind.</summary>
    [JsonConstructor]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PortiaCreateOutcome(T value, PortiaCreateOutcomeKind kind)
    {
        Value = value;
        Kind = PortiaCreateOutcome.ValidateKind(kind);
    }

    /// <summary>The response payload.</summary>
    public T Value { get; }

    /// <summary>The decision established by the handler.</summary>
    public PortiaCreateOutcomeKind Kind { get; }

    object? IPortiaCreateOutcome.UntypedValue => Value;

    JsonTypeInfo? IPortiaCreateOutcome.PayloadTypeInfo(JsonSerializerOptions options) => options.GetTypeInfo<T>();

#pragma warning disable CA1000
    /// <summary>Creates a newly committed outcome.</summary>
    public static PortiaCreateOutcome<T> Created(T value) => new(value, PortiaCreateOutcomeKind.Created);

    /// <summary>Creates an outcome after confirming an identical earlier request.</summary>
    public static PortiaCreateOutcome<T> Repeated(T value) => new(value, PortiaCreateOutcomeKind.Repeated);

    /// <summary>Creates a committed replacement outcome.</summary>
    public static PortiaCreateOutcome<T> Replaced(T value) => new(value, PortiaCreateOutcomeKind.Replaced);
#pragma warning restore CA1000
}
