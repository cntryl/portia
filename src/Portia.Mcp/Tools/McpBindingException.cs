namespace Cntryl.Portia;

sealed class McpBindingException(Exception innerException)
    : Exception("The MCP tool input is invalid.", innerException);
