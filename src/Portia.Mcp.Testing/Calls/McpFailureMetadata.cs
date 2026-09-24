namespace Cntryl.Portia.Testing;

// The result metadata key under which Portia's MCP tools report failure details. It matches the key the server
// writes; the testing package does not reference the server package, so it names the key itself.
static class McpFailureMetadata
{
    public const string Key = "portia/error";
}
