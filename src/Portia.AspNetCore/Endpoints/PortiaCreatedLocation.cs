using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Cntryl.Portia;

abstract record PortiaCreatedLocation(bool PayloadIsNullable, bool IncludePayload);

sealed partial record PortiaCreatedLocation<TRequest> : PortiaCreatedLocation
{
    readonly string _template;
    readonly Placeholder[] _placeholders;

    public PortiaCreatedLocation(string template, bool payloadIsNullable, bool includePayload, RoutePattern route,
        IReadOnlyDictionary<string, Func<TRequest, object?>> requestMembers,
        IReadOnlyDictionary<string, Func<object?, object?>>? payloadMembers)
        : base(payloadIsNullable, includePayload)
    {
        _template = template;
        var pattern = PlaceholderPattern();
        if (pattern.Replace(template, string.Empty).AsSpan().ContainsAny('{', '}'))
            throw new InvalidOperationException("Created URI template contains malformed placeholders.");

        var routeNames = route.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _placeholders = pattern.Matches(template).Select(match =>
        {
            var name = match.Groups[1].Value;
            var isRouteValue = routeNames.Contains(name);
            var requestGetter = isRouteValue ? null : FindMember(name, requestMembers);
            var payloadGetter = isRouteValue || requestGetter is not null || payloadMembers is null
                ? null
                : FindMember(name, payloadMembers);
            if (!isRouteValue && requestGetter is null && payloadGetter is null)
                throw new InvalidOperationException(
                    $"Created URI template placeholder '{name}' does not match a route parameter or a readable request or payload member.");
            return new Placeholder(name, match.Index, match.Length, requestGetter, payloadGetter);
        }).ToArray();
    }

    public Func<object?, string> Resolve(HttpContext context, TRequest request)
    {
        var values = new string?[_placeholders.Length];
        for (var i = 0; i < _placeholders.Length; i++)
        {
            var placeholder = _placeholders[i];
            if (placeholder.PayloadGetter is not null)
                continue;

            var value = placeholder.RequestGetter is { } requestGetter
                ? requestGetter(request)
                : context.Request.RouteValues.GetValueOrDefault(placeholder.Name);
            var segment = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(segment))
                throw new BadHttpRequestException(
                    $"No value was supplied for Created URI placeholder '{placeholder.Name}'.");
            values[i] = Uri.EscapeDataString(segment);
        }

        return payload =>
        {
            for (var i = 0; i < _placeholders.Length; i++)
            {
                var placeholder = _placeholders[i];
                if (placeholder.PayloadGetter is not { } payloadGetter)
                    continue;
                var segment = Convert.ToString(payload is null ? null : payloadGetter(payload),
                    CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(segment))
                    throw new InvalidOperationException(
                        $"The create payload did not provide a value for Created URI placeholder '{placeholder.Name}'.");
                values[i] = Uri.EscapeDataString(segment);
            }

            var location = new StringBuilder(_template.Length);
            var offset = 0;
            for (var i = 0; i < _placeholders.Length; i++)
            {
                var placeholder = _placeholders[i];
                _ = location.Append(_template, offset, placeholder.Index - offset).Append(values[i]);
                offset = placeholder.Index + placeholder.Length;
            }

            return location.Append(_template, offset, _template.Length - offset).ToString();
        };
    }

    static Func<TRequest, object?>? FindMember(string name,
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
                throw new InvalidOperationException(
                    $"Created URI template placeholder '{name}' matches multiple request members.");
            match = getter;
        }

        return match;
    }

    static Func<object?, object?>? FindMember(string name,
        IReadOnlyDictionary<string, Func<object?, object?>> members)
    {
        var normalizedName = name.Replace("_", string.Empty, StringComparison.Ordinal);
        Func<object?, object?>? match = null;
        foreach (var (member, getter) in members)
        {
            if (!string.Equals(member.Replace("_", string.Empty, StringComparison.Ordinal), normalizedName,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (match is not null)
                throw new InvalidOperationException(
                    $"Created URI template placeholder '{name}' matches multiple payload members.");
            match = getter;
        }

        return match;
    }

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex PlaceholderPattern();

    sealed record Placeholder(string Name, int Index, int Length, Func<TRequest, object?>? RequestGetter,
        Func<object?, object?>? PayloadGetter);
}
