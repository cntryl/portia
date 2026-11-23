using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cntryl.Portia;

// JSON Schema carries portable constraints. Extensions retain .NET-specific rules without
// pretending that a different regex engine or a format annotation enforces the runtime rule.
static class PortiaJsonConstraints
{
    internal const string RequiredTextPattern = @"[^\u0009-\u000D\u0020\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000]";

    internal static void Apply(JsonObject schema, PortiaContractMemberMetadata member, JsonSerializerOptions options)
    {
        if (!member.Input && member.Output)
            schema["readOnly"] = true;

        if (member.Input && !member.Output)
            schema["writeOnly"] = true;

        if (member.HasBindingDefault || member.HasDefault)
        {
            var value = member.HasBindingDefault ? member.BindingDefaultValue : member.DefaultValue;

            if (member.Rules.All(rule => PortiaContractValidation.IsValid(member, rule, value)))
                schema["default"] = JsonSerializer.SerializeToNode(value, options.GetTypeInfo(member.Type));
            else
                _ = schema.Remove("default");
        }


        foreach (var rule in member.Rules)
        {
            switch (rule)
            {
                case RequiredAttribute required:
                    ExcludeNull(schema);
                    if (!required.AllowEmptyStrings && HasType(schema, "string"))
                    {
                        Lower(schema, "minLength", 1);
                        schema["x-portia-reject-whitespace"] = true;
                    }

                    break;
                case DecimalStringRangeAttribute decimalRange:
                    schema["x-portia-range"] = new JsonObject
                    {
                        ["minimum"] = decimalRange.Minimum,
                        ["maximum"] = decimalRange.Maximum,
                        ["operandType"] = typeof(DecimalString).FullName,
                        ["minimumIsExclusive"] = decimalRange.MinimumIsExclusive,
                        ["maximumIsExclusive"] = decimalRange.MaximumIsExclusive
                    };
                    break;
                case RangeAttribute range:
                    schema["x-portia-range"] = new JsonObject
                    {
                        ["minimum"] = JsonValue.Create(Convert.ToString(range.Minimum, CultureInfo.InvariantCulture)),
                        ["maximum"] = JsonValue.Create(Convert.ToString(range.Maximum, CultureInfo.InvariantCulture)),
                        ["operandType"] = range.OperandType?.FullName,
                        ["minimumIsExclusive"] = range.MinimumIsExclusive,
                        ["maximumIsExclusive"] = range.MaximumIsExclusive
                    };
                    var valueType = Nullable.GetUnderlyingType(member.Type) ?? member.Type;
                    if (valueType.IsEnum)
                        valueType = Enum.GetUnderlyingType(valueType);
                    var roundsFractional = range.Minimum is int && (valueType == typeof(double) ||
                                                                    valueType == typeof(float) ||
                                                                    valueType == typeof(decimal));
                    // BCL comparison converts to double, while decimal/Int64 JSON values retain
                    // their precision and Single JSON uses its shortest round-trip representation.
                    var losesPrecision = range.Minimum is double lower && range.Maximum is double upper &&
                                         (valueType == typeof(decimal) || valueType == typeof(float) ||
                                          (valueType == typeof(long) || valueType == typeof(ulong)) &&
                                          (Math.Abs(lower) >= 9_007_199_254_740_992D ||
                                           Math.Abs(upper) >= 9_007_199_254_740_992D));
                    if (!roundsFractional && !losesPrecision &&
                        (HasType(schema, "number") || HasType(schema, "integer")))
                    {
                        Bound(schema, range.MinimumIsExclusive ? "exclusiveMinimum" : "minimum", range.Minimum);
                        Bound(schema, range.MaximumIsExclusive ? "exclusiveMaximum" : "maximum", range.Maximum);
                    }

                    break;
                case StringLengthAttribute length when HasType(schema, "string"):
                    StringMinimum(schema, length.MinimumLength);
                    StringMaximum(schema, length.MaximumLength);
                    break;
                case MinLengthAttribute minimum:
                    if (HasType(schema, "string"))
                        StringMinimum(schema, minimum.Length);
                    else if (LengthKeyword(schema, "min") is { } minimumKeyword)
                        Lower(schema, minimumKeyword, minimum.Length);
                    break;
                case MaxLengthAttribute { Length: >= 0 } maximum:
                    if (HasType(schema, "string"))
                        StringMaximum(schema, maximum.Length);
                    else if (LengthKeyword(schema, "max") is { } maximumKeyword)
                        Upper(schema, maximumKeyword, maximum.Length);
                    break;
                case RegularExpressionAttribute expression when HasType(schema, "string"):
                    schema["x-portia-dotnet-pattern"] = expression.Pattern;
                    // The BCL checks the length of its first match. An atomic lookahead captures
                    // that match before the end check can backtrack into a longer alternative.
                    if (PortablePattern(expression.Pattern))
                        schema["pattern"] = "^(?:(?=(" + expression.Pattern + "))\\1|)(?![\\s\\S])";
                    break;
                case EmailAddressAttribute when HasType(schema, "string"):
                    schema["format"] = "email";
                    break;
                case UrlAttribute when HasType(schema, "string"):
                    schema["format"] = "uri";
                    break;
            }
        }
    }

    internal static bool RequiresPresence(PortiaContractMemberMetadata? member) => member is not null &&
        (member.HasBindingDefault || member.ConstructorBound) &&
        member.Rules.Any(rule => !PortiaContractValidation.IsValid(member, rule, member.BindingDefaultValue));

    static bool HasType(JsonObject schema, string name) => schema["type"] is JsonArray array
        ? array.Any(value => value?.GetValue<string>() == name)
        : schema["type"] is JsonValue value && value.TryGetValue<string>(out var type) && type == name;

    static string? LengthKeyword(JsonObject schema, string prefix) => HasType(schema, "string") ? prefix + "Length"
        : HasType(schema, "array") ? prefix + "Items"
        : HasType(schema, "object") ? prefix + "Properties" : null;

    static void StringMinimum(JsonObject schema, int units)
    {
        // Each Unicode scalar consumes one or two UTF-16 units. This portable lower bound
        // must never reject a value accepted by .NET; the extension retains the exact rule.
        Lower(schema, "minLength", units / 2 + units % 2);
        Lower(schema, "x-portia-utf16-minLength", units);
    }

    static void StringMaximum(JsonObject schema, int units)
    {
        Upper(schema, "maxLength", units);
        Upper(schema, "x-portia-utf16-maxLength", units);
    }

    static void Lower(JsonObject schema, string name, int value) =>
        schema[name] = Math.Max(schema[name]?.GetValue<int>() ?? 0, value);

    static void Upper(JsonObject schema, string name, int value) =>
        schema[name] = Math.Min(schema[name]?.GetValue<int>() ?? int.MaxValue, value);

    static void Bound(JsonObject schema, string keyword, object bound)
    {
        var number = Convert.ToDouble(bound, CultureInfo.InvariantCulture);

        if (double.IsFinite(number))
            schema[keyword] = JsonValue.Create(number);
    }

    static bool PortablePattern(string pattern)
    {
        if (pattern.Contains("(?", StringComparison.Ordinal))
            return false;

        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];

            if (character > 127 || character is '\\' or '.' or '$' or '^' or ']' or '}')
                return false;

            if (character == '{')
            {
                var end = pattern.IndexOf('}', index + 1);

                if (end < 0)
                    return false;

                var bounds = pattern.AsSpan(index + 1, end - index - 1);
                var comma = bounds.IndexOf(',');
                var minimum = comma < 0 ? bounds : bounds[..comma];

                if (minimum.IsEmpty || minimum.ContainsAnyExceptInRange('0', '9') ||
                    comma >= 0 && bounds[(comma + 1)..].ContainsAnyExceptInRange('0', '9'))
                    return false;

                index = end;
                continue;
            }

            if (character != '[')
                continue;
            // Only unambiguous ASCII literals/ranges are portable inside classes. In .NET,
            // a leading ']' is a literal and nested '[' can introduce class subtraction.
            var count = 0;

            while (++index < pattern.Length && pattern[index] != ']')
            {
                if (!char.IsAsciiLetterOrDigit(pattern[index]))
                    return false;

                count++;

                if (index + 1 < pattern.Length && pattern[index + 1] == '-')
                {
                    if (index + 2 >= pattern.Length || !char.IsAsciiLetterOrDigit(pattern[index + 2]) ||
                        pattern[index + 2] < pattern[index])
                        return false;

                    index += 2;
                }
            }


            if (count == 0 || index == pattern.Length)
                return false;
        }

        return true;
    }

    static void ExcludeNull(JsonObject schema)
    {
        if (schema["type"] is JsonArray types)
        {
            for (var index = types.Count - 1; index >= 0; index--)
                if (types[index]?.GetValue<string>() == "null")
                    types.RemoveAt(index);

            if (types.Count == 1)
                schema["type"] = types[0]!.DeepClone();
        }
        else if (!schema.ContainsKey("type"))
        {
            var existing = schema["not"]?.DeepClone();
            schema["not"] = existing is null
                ? new JsonObject { ["type"] = "null" }
                : new JsonObject { ["anyOf"] = new JsonArray(existing, new JsonObject { ["type"] = "null" }) };
        }
    }
}
