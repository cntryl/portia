using System.Buffers;

namespace Cntryl.Portia;

sealed record PortiaHeaderMembers(IReadOnlyList<PortiaHeaderMember> Members);

sealed record PortiaHeaderMember(string MemberName, string Name);

/// <summary>A contract member that every implementing request binds from one HTTP header.</summary>
sealed record PortiaHeaderBinding(Type Contract, string Member, string Name);

static class PortiaHttpHeaderName
{
    // RFC 9110 section 5.6.2: a field name is a token.
    static readonly SearchValues<char> Token = SearchValues.Create(
        "!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

    // A bound request is queued and persisted under Prefer: respond-async, so a credential must never
    // become a request member. OpenAPI ignores Accept, Content-Type and Authorization declared as
    // parameters, and Portia interprets Prefer itself.
    static readonly HashSet<string> Reserved =
    [
        with(StringComparer.OrdinalIgnoreCase), "Authorization", "Proxy-Authorization", "Cookie", "Accept",
        "Content-Type", "Prefer"
    ];

    internal static void Validate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.AsSpan().ContainsAnyExcept(Token))
            throw new ArgumentException($"'{name}' is not a valid HTTP header name.", nameof(name));
        if (Reserved.Contains(name))
            throw new ArgumentException($"HTTP header '{name}' cannot be bound to a request member.", nameof(name));
    }
}
