using Cntryl.Portia;
using System.Text.Json.Serialization;

#pragma warning disable CA1707 // Deliberately reproduce the namespace/context-name collision.
namespace App_A
{
    /// <summary>A contract covered by the underscore namespace context.</summary>
    [System.ComponentModel.Description("Referenced payload.")]
    [System.ComponentModel.DisplayName("Referenced payload title")]
    public sealed record FirstPayload([property: System.ComponentModel.Description("Referenced value."), System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(64, MinimumLength = 1), System.ComponentModel.DataAnnotations.Display(Name = "Referenced value title")] string Value);

    [PortiaJsonContext, JsonSerializable(typeof(FirstPayload))]
    sealed partial class Context : JsonSerializerContext;
}

#pragma warning restore CA1707

namespace App
{
    /// <summary>A contract covered by a formerly colliding context name.</summary>
    public sealed record SecondPayload([property: System.ComponentModel.DataAnnotations.Range(0, 100), System.ComponentModel.DefaultValue(5)] int Value = 5);

    [PortiaJsonContext, JsonSerializable(typeof(SecondPayload))]
    sealed partial class A_Context : JsonSerializerContext;
}
