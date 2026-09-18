namespace Cntryl.Portia;

sealed record PortiaStreamProducesMetadata(int StatusCode, Type Type, IReadOnlyList<string> ContentTypes);
