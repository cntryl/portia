using System.ComponentModel;

namespace Cntryl.Portia;

/// <summary>Compile-time endpoint shape consumed by Portia's Microsoft OpenAPI transformer.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record PortiaOpenApiOperation(
    string OperationId,
    Type? ResultType,
    bool NoContent,
    bool JsonStream,
    bool ServerSentEvents,
    bool QueueCapable,
    IReadOnlyList<PortiaOpenApiParameter> Parameters);
