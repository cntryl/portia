using System.ComponentModel;
using Microsoft.AspNetCore.Builder;

namespace Cntryl.Portia;

/// <summary>
///     Records, for one mapped endpoint, which request members generated binding reads only from the
///     query string or only from an HTTP header. Resolved once when the endpoint is mapped.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class PortiaHttpMemberSources
{
    readonly PortiaHttpMember[] _members;
    readonly bool[] _query;
    readonly string?[] _headers;

    PortiaHttpMemberSources(PortiaHttpMember[] members, bool[] query, string?[] headers)
    {
        _members = members;
        _query = query;
        _headers = headers;
    }

    /// <summary>Reports whether a request member binds only from the query string.</summary>
    /// <param name="index">The member's constructor position.</param>
    /// <returns><see langword="true" /> when the member was declared with <c>FromQuery</c>.</returns>
    public bool Query(int index) => _query[index];

    /// <summary>Gets the HTTP header a request member binds from.</summary>
    /// <param name="index">The member's constructor position.</param>
    /// <returns>The header name, or <see langword="null" /> when the member is not header-bound.</returns>
    public string? Header(int index) => _headers[index];

    /// <summary>Describes the resolved member sources to OpenAPI.</summary>
    /// <param name="builder">The mapped endpoint.</param>
    public void ApplyMetadata(IEndpointConventionBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var query = new List<string>();
        var headers = new List<PortiaHeaderMember>();

        for (var index = 0; index < _members.Length; index++)
        {
            if (_query[index])
                query.Add(_members[index].Name);

            if (_headers[index] is { } header)
                headers.Add(new PortiaHeaderMember(_members[index].Name, header));
        }


        if (query.Count != 0)
            builder.Add(endpoint => endpoint.Metadata.Add(new PortiaQueryMembers(query)));

        if (headers.Count != 0)
            builder.Add(endpoint => endpoint.Metadata.Add(new PortiaHeaderMembers(headers)));
    }

    // A custom binder owns every input, so neither endpoint nor application-wide sources apply.
    internal static PortiaHttpMemberSources None(PortiaHttpMember[] members) =>
        new(members, new bool[members.Length], new string?[members.Length]);

    internal static PortiaHttpMemberSources Resolve(
        Type requestType,
        PortiaHttpMember[] members,
        IReadOnlyList<string> queryMembers,
        IReadOnlyDictionary<string, string> headerMembers,
        IReadOnlyList<PortiaHeaderBinding> applicationHeaders
    )
    {
        var query = new bool[members.Length];
        var headers = new string?[members.Length];

        foreach (var name in queryMembers)
            query[Redirectable(members, name, "FromQuery")] = true;


        foreach (var (name, header) in headerMembers)
        {
            var index = Redirectable(members, name, "FromHeader");

            if (query[index])
                throw new InvalidOperationException(
                    $"Request member '{name}' cannot bind from both a query parameter and an HTTP header.");

            headers[index] = header;
        }

        foreach (var binding in applicationHeaders)
        {
            if (!binding.Contract.IsAssignableFrom(requestType))
                continue;

            var index = Array.FindIndex(members,
                item => string.Equals(item.Name, binding.Member, StringComparison.OrdinalIgnoreCase));

            if (index < 0)
                throw new InvalidOperationException(
                    $"'{requestType.Name}' implements '{binding.Contract.Name}', but '{binding.Member}' is not a constructor-bound request member.");

            var bound = $"Request member '{binding.Member}' of '{requestType.Name}' is bound from header '{binding.Name}' application-wide and";

            if (members[index].Source == "route")
                throw new InvalidOperationException($"{bound} cannot bind from the route.");

            if (!members[index].Scalar)
                throw new InvalidOperationException($"{bound} must be a string, enum, or TryParse scalar.");

            if (query[index])
                throw new InvalidOperationException($"{bound} cannot also be declared FromQuery.");


            if (headers[index] is { } declared && !string.Equals(declared, binding.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{bound} cannot also bind from header '{declared}'.");

            headers[index] = binding.Name;
        }

        for (var index = 0; index < headers.Length; index++)
        {
            if (headers[index] is { } header && Array.FindIndex(headers, index + 1,
                    other => string.Equals(other, header, StringComparison.OrdinalIgnoreCase)) >= 0)
                throw new InvalidOperationException(
                    $"HTTP header '{header}' is bound to more than one member of '{requestType.Name}'.");
        }

        return new PortiaHttpMemberSources(members, query, headers);
    }

    static int Redirectable(PortiaHttpMember[] members, string name, string declaration)
    {
        var index = Array.FindIndex(members,
            item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
            throw new InvalidOperationException(
                $"{declaration} member '{name}' is not a constructor-bound request member.");

        if (members[index].Source == "route")
            throw new InvalidOperationException($"{declaration} member '{name}' is already bound from the route.");

        if (!members[index].Scalar)
            throw new InvalidOperationException(
                $"{declaration} member '{name}' must be a string, enum, or TryParse scalar.");

        return index;
    }
}
