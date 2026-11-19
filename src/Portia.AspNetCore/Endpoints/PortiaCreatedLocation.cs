using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Cntryl.Portia;

abstract record PortiaCreatedLocation(bool PayloadIsNullable);

sealed partial record PortiaCreatedLocation<TRequest> : PortiaCreatedLocation
{
    readonly string _template;
    readonly Placeholder[] _placeholders;

    public PortiaCreatedLocation(string template, bool payloadIsNullable, RoutePattern route,
        IReadOnlyDictionary<string, Func<TRequest, object?>> requestMembers)
        : base(payloadIsNullable)
    {
        _template = template;
        var pattern = PlaceholderPattern();
        if (pattern.Replace(template, string.Empty).AsSpan().ContainsAny('{', '}'))
            throw new InvalidOperationException("Created URI template contains malformed placeholders.");

        var routeNames = route.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _placeholders = pattern.Matches(template).Select(match =>
        {
            var name = match.Groups[1].Value;
            // A matching route parameter owns this placeholder, even if it is absent on a request.
            var getter = routeNames.Contains(name) ? null : FindMember(name, requestMembers);
            return new Placeholder(name, match.Index, match.Length, getter);
        }).ToArray();
    }

    public string Resolve(HttpContext context, TRequest request)
    {
        if (_placeholders.Length == 0)
            return _template;

        var location = new StringBuilder(_template.Length);
        var offset = 0;
        foreach (var placeholder in _placeholders)
        {
            var value = placeholder.Getter is { } getter
                ? getter(request)
                : context.Request.RouteValues.GetValueOrDefault(placeholder.Name);
            var segment = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(segment))
                throw new BadHttpRequestException($"No value was supplied for Created URI placeholder '{placeholder.Name}'.");

            _ = location.Append(_template, offset, placeholder.Index - offset).Append(Uri.EscapeDataString(segment));
            offset = placeholder.Index + placeholder.Length;
        }

        return location.Append(_template, offset, _template.Length - offset).ToString();
    }

    static Func<TRequest, object?> FindMember(string name,
        IReadOnlyDictionary<string, Func<TRequest, object?>> members)
    {
        var normalizedName = name.Replace("_", string.Empty, StringComparison.Ordinal);
        Func<TRequest, object?>? match = null;
        foreach (var (member, getter) in members)
        {
            if (!string.Equals(member.Replace("_", string.Empty, StringComparison.Ordinal), normalizedName,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (match is not null)
                throw new InvalidOperationException($"Created URI template placeholder '{name}' matches multiple request members.");
            match = getter;
        }

        return match ?? throw new InvalidOperationException(
            $"Created URI template placeholder '{name}' does not match a route parameter or a readable request member.");
    }

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex PlaceholderPattern();

    sealed record Placeholder(string Name, int Index, int Length, Func<TRequest, object?>? Getter);
}
