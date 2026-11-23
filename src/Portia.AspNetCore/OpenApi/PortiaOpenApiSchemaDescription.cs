using System.Linq.Expressions;
using System.Reflection;

namespace Cntryl.Portia;

/// <summary>Describes direct CLR members of one JSON contract type.</summary>
public sealed class PortiaOpenApiSchemaDescription<T>
{
    readonly Dictionary<string, string> _properties = [with(StringComparer.OrdinalIgnoreCase)];

    internal PortiaOpenApiSchemaDescription()
    {
    }

    /// <summary>Describes a direct member, such as <c>x =&gt; x.Name</c>, independently of its serialized name.</summary>
    public PortiaOpenApiSchemaDescription<T> Property<TMember>(Expression<Func<T, TMember>> member, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        var name = PortiaOpenApiMemberSelector.Name(member);

        if (!_properties.TryAdd(name, description))
            throw new InvalidOperationException($"OpenAPI property '{typeof(T)}.{name}' is described more than once.");

        return this;
    }

    internal IReadOnlyDictionary<string, string> Snapshot() =>
        new Dictionary<string, string>(_properties, StringComparer.OrdinalIgnoreCase);
}

static class PortiaOpenApiMemberSelector
{
    internal static string Name<T, TMember>(Expression<Func<T, TMember>> member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var body = member.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert
            ? convert.Operand
            : member.Body;
        return body is MemberExpression { Member: PropertyInfo or FieldInfo } access &&
               access.Expression == member.Parameters[0]
            ? access.Member.Name
            : throw new ArgumentException("Select a direct member, such as x => x.Name.", nameof(member));
    }
}
