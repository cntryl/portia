using System.Linq.Expressions;

namespace Cntryl.Portia;

/// <summary>Controls Portia's generated HTTP boundary.</summary>
public sealed class PortiaHttpOptions
{
    /// <summary>Gets explicit OpenAPI descriptions and optional coverage enforcement.</summary>
    public PortiaOpenApiOptions OpenApi { get; } = new();

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

    /// <summary>
    ///     Gets or sets application-owned nullability for array elements or dictionary values when
    ///     <see cref="OpenApiSchemaType" /> maps a contract to a collection representation.
    /// </summary>
    /// <remarks>
    ///     C# reference nullability is erased from <see cref="Type" />. Return <see langword="true" />
    ///     or <see langword="false" /> when the representation's element or value nullability is
    ///     declared by a converter but absent from serializer type metadata; return <see langword="null" />
    ///     to use the serializer metadata. The callback receives the mapped contract type and the
    ///     representation's element or value type.
    /// </remarks>
    public Func<Type, Type, bool?>? OpenApiSchemaCollectionElementIsNullable { get; set; }

    /// <summary>
    ///     Binds a member of an application contract from one HTTP header on every Portia endpoint whose
    ///     request implements that contract, and describes the header in OpenAPI.
    /// </summary>
    /// <typeparam name="TContract">The application's contract, such as <c>ITenantRequest</c>.</typeparam>
    /// <param name="member">The contract member, such as <c>x =&gt; x.TenantId</c>.</param>
    /// <param name="name">The HTTP header name, such as <c>Tenant-Id</c>.</param>
    /// <returns>These options.</returns>
    /// <remarks>
    ///     The binding always applies: mapping fails when an endpoint binds the member from its route,
    ///     declares it <c>FromQuery</c>, or names a different header. An endpoint with <c>OnBind</c>
    ///     owns its inputs and is unaffected. The member must be a string, enum, or <c>TryParse</c>
    ///     scalar. Declare bindings before mapping endpoints.
    /// </remarks>
    /// <exception cref="ArgumentException">
    ///     The selector is not a direct member, or the name is not a bindable HTTP header name.
    /// </exception>
    /// <exception cref="InvalidOperationException">The contract member is already bound.</exception>
    public PortiaHttpOptions FromHeader<TContract>(Expression<Func<TContract, object?>> member, string name)
        where TContract : class
    {
        var memberName = PortiaHttpMemberSelector.Name(member,
            "FromHeader requires a direct contract member, such as x => x.TenantId.");
        PortiaHttpHeaderName.Validate(name);

        if (HeaderBindings.Any(item => item.Contract == typeof(TContract) &&
                                       string.Equals(item.Member, memberName, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"FromHeader for '{typeof(TContract).Name}.{memberName}' is declared more than once.");

        HeaderBindings.Add(new PortiaHeaderBinding(typeof(TContract), memberName, name));
        return this;
    }

    internal List<PortiaHeaderBinding> HeaderBindings { get; } = [];

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
