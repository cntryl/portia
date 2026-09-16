using Cntryl.Portia.Testing;

Console.WriteLine(typeof(McpScenario).Assembly.GetName().Name == "Portia.Mcp.Testing"
    ? "MCP testing package consumer passed."
    : throw new InvalidOperationException("The packed MCP testing assembly was not loaded."));
