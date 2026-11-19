using Microsoft.AspNetCore.Http.Metadata;

namespace Cntryl.Portia;

sealed record PortiaCreatedResponseMetadata(int StatusCode, Type? PayloadType, string[] ContentTypes)
    : IProducesResponseTypeMetadata
{
    Type? IProducesResponseTypeMetadata.Type => null;

    IEnumerable<string> IProducesResponseTypeMetadata.ContentTypes => ContentTypes;
}
