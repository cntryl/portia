namespace Cntryl.Portia;

/// <summary>Controls Portia's generated HTTP boundary.</summary>
public sealed class PortiaHttpOptions
{
    /// <summary>Gets the default maximum JSON request-body size (10 MiB).</summary>
    public const long DefaultMaxJsonBodyBytes = 10 * 1024 * 1024;

    /// <summary>Gets or sets the maximum JSON request-body size.</summary>
    public long MaxJsonBodyBytes { get; set; } = DefaultMaxJsonBodyBytes;

    /// <summary>
    ///     Gets or sets whether Portia maps <c>/openapi/v1.json</c> and <c>/openapi/v1.yml</c>.
    ///     Defaults to <see langword="true" />.
    ///     <para>
    ///         Setting this to <see langword="false" /> stops Portia from mapping those two routes; it
    ///         does not stop the document being composed. An application that wants the document on a
    ///         different path, behind authorization, or on a separate port turns this off and calls
    ///         <c>MapOpenApi</c> itself — the document it gets is the same one, with Portia's operation
    ///         IDs, parameters, and responses.
    ///     </para>
    ///     <para>
    ///         Whether a schema should be publicly reachable is a deployment decision, not a framework
    ///         one. Publishing it is not itself a disclosure — every mapped endpoint is one the
    ///         application opted into with <see cref="ICallable" /> — but the choice belongs to whoever
    ///         operates the application.
    ///     </para>
    /// </summary>
    public bool ServeOpenApi { get; set; } = true;

    /// <summary>Gets the default interval between server-sent-event keep-alive comments (15 seconds).</summary>
    public static readonly TimeSpan DefaultServerSentEventKeepAlive = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Gets or sets how often an idle server-sent-event stream emits a comment, or
    ///     <see langword="null" /> to emit none. Defaults to
    ///     <see cref="DefaultServerSentEventKeepAlive" />.
    ///     <para>
    ///         A stream that is quiet is the normal state of an event source, and an idle connection
    ///         is what proxies, load balancers and browsers reclaim. The comment is the framing's own
    ///         no-op — a line beginning <c>:</c> that every client already ignores — so keeping the
    ///         connection alive costs nothing a consumer has to know about.
    ///     </para>
    /// </summary>
    public TimeSpan? ServerSentEventKeepAlive { get; set; } = DefaultServerSentEventKeepAlive;
}
