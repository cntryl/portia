using Cntryl.Portia;
using System.Text.Json.Serialization;

#pragma warning disable CA1707 // Deliberately reproduce the namespace/context-name collision.
namespace App_A
{
    /// <summary>A contract covered by the underscore namespace context.</summary>
    [System.ComponentModel.Description("Referenced payload.")]
    public sealed record FirstPayload([property: System.ComponentModel.Description("Referenced value.")] string Value);

    [PortiaJsonContext, JsonSerializable(typeof(FirstPayload))]
    sealed partial class Context : JsonSerializerContext;
}

#pragma warning restore CA1707

namespace App
{
    /// <summary>A contract covered by a formerly colliding context name.</summary>
    public sealed record SecondPayload(int Value);

    [PortiaJsonContext, JsonSerializable(typeof(SecondPayload))]
    sealed partial class A_Context : JsonSerializerContext;
}
