namespace Cntryl.Portia;

/// <summary>Controls Portia's generated HTTP boundary.</summary>
public sealed class PortiaHttpOptions
{
    /// <summary>Gets the default maximum JSON request-body size (10 MiB).</summary>
    public const long DefaultMaxJsonBodyBytes = 10 * 1024 * 1024;

    /// <summary>Gets the default interval between server-sent-event keep-alive comments (15 seconds).</summary>
    public static readonly TimeSpan DefaultServerSentEventKeepAlive = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Gets or sets an application-owned mapping from a JSON contract type to its wire representation
    ///     type for Portia OpenAPI export. Return null to keep the ordinary serializer schema.
    /// </summary>
    /// <remarks>
    ///     Use this for converter-backed types whose JSON shape the serializer cannot infer. Both types
    ///     must have metadata in Portia's configured JSON resolver. The mapping changes documentation
    ///     only; converters still own runtime JSON. Nullable value types reuse their underlying type's
    ///     mapping unless explicitly overridden. Property nullability, defaults and requiredness are
    ///     retained. Configure the mapping before the first document request; documents are cached.
    /// </remarks>
    public Func<Type, Type?>? OpenApiSchemaType { get; set; }

    /// <summary>Gets or sets the maximum JSON request-body size.</summary>
    public long MaxJsonBodyBytes { get; set; } = DefaultMaxJsonBodyBytes;

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
