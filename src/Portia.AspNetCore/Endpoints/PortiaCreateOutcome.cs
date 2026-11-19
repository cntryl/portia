using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Cntryl.Portia;

interface IPortiaCreateOutcome
{
    object? UntypedValue { get; }
    JsonTypeInfo PayloadTypeInfo(JsonSerializerOptions options);
    bool IsCreated { get; }
}

/// <summary>Distinguishes a committed create from a handler-confirmed identical repeat.</summary>
public sealed class PortiaCreateOutcome<T> : IPortiaCreateOutcome
{
    PortiaCreateOutcome(T value, bool created) { Value = value; IsCreated = created; }
    /// <summary>The response payload.</summary>
    public T Value { get; }
    /// <summary>Whether this call committed a new resource.</summary>
    public bool IsCreated { get; }
    object? IPortiaCreateOutcome.UntypedValue => Value;
    JsonTypeInfo IPortiaCreateOutcome.PayloadTypeInfo(JsonSerializerOptions options) => options.GetTypeInfo<T>();
#pragma warning disable CA1000
    /// <summary>Creates a newly committed outcome.</summary>
    public static PortiaCreateOutcome<T> Created(T value) => new(value, true);
    /// <summary>Creates an outcome after confirming an identical earlier request.</summary>
    public static PortiaCreateOutcome<T> Repeated(T value) => new(value, false);
#pragma warning restore CA1000
}
