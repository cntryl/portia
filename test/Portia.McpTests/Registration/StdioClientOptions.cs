using ModelContextProtocol.Client;

namespace Cntryl.Portia.Tests;

/// <summary>Client options for tests that start the stdio host as a child process.</summary>
static class StdioClientOptions
{
    /// <summary>
    ///     A cold <c>dotnet</c> child can take longer than the SDK's five-second <c>server/discover</c>
    ///     probe to answer, after which the client wrongly concludes the host needs <c>initialize</c>.
    ///     The probe is therefore bounded only by the initialization timeout.
    /// </summary>
    /// <param name="revision">The protocol revision to request, or <see langword="null" /> for the default.</param>
    /// <returns>Options for one stdio client.</returns>
    public static McpClientOptions Create(string? revision = null) =>
        new() { ProtocolVersion = revision, DiscoverProbeTimeout = Timeout.InfiniteTimeSpan };
}
