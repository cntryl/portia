namespace Cntryl.Portia;

/// <summary>Keys of the keyed services Portia registers.</summary>
public static class PortiaServiceKeys
{
    /// <summary>
    ///     Keys Portia's <see cref="System.Text.Json.JsonSerializerOptions" />, the wire format of its events,
    ///     requests, and HTTP and MCP payloads. It is keyed so that an application's own
    ///     <see cref="System.Text.Json.JsonSerializerOptions" /> registration never changes Portia's wire format,
    ///     and Portia's never becomes the application's.
    /// </summary>
    public const string Json = "Cntryl.Portia.Json";
}
