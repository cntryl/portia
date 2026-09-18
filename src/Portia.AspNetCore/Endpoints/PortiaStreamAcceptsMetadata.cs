namespace Cntryl.Portia;

sealed record PortiaStreamAcceptsMetadata(IReadOnlyList<string> ContentTypes, Type RequestType, bool IsOptional);
