using ModelContextProtocol.Protocol;

namespace Cntryl.Portia;

readonly record struct McpToolInvocationResult(CallToolResult Result, RequestError? Error);
