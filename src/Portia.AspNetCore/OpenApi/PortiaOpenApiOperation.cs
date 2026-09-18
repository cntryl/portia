using System.ComponentModel;

namespace Cntryl.Portia;

/// <summary>Compile-time endpoint shape consumed by Portia's Microsoft OpenAPI transformer.</summary>
/// <param name="OperationId">The document-unique operation identifier.</param>
/// <param name="ResultType">
///     The CLR type of the success payload, or <see langword="null" /> when the
///     operation produces none.
/// </param>
/// <param name="NoContent">Whether a successful response has an empty body.</param>
/// <param name="JsonStream">Whether the response is an incrementally flushed JSON array.</param>
/// <param name="ServerSentEvents">Whether the response is a <c>text/event-stream</c> body.</param>
/// <param name="QueueCapable">Whether the endpoint honours <c>Prefer: respond-async</c>.</param>
/// <param name="Parameters">The generated constructor bindings, in declaration order.</param>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed record PortiaOpenApiOperation(
    string OperationId,
    Type? ResultType,
    bool NoContent,
    bool JsonStream,
    bool ServerSentEvents,
    bool QueueCapable,
    IReadOnlyList<PortiaOpenApiParameter> Parameters);
