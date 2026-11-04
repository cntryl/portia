using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cntryl.Portia;

/// <summary>Supplies Portia's built-in processor-status JSON metadata to generated application composition.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ProcessorStatusJsonContextFactory
{
    /// <summary>Creates the built-in status context using the application's composed Portia JSON options.</summary>
    /// <param name="options">The shared serialization options.</param>
    /// <returns>The source-generated status context.</returns>
    public static JsonSerializerContext Create(JsonSerializerOptions options) => new ProcessorStatusJsonContext(options);
}
