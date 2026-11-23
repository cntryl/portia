using System.Globalization;
using System.Text.Json.Serialization;

namespace Cntryl.Portia;

/// <summary>A decimal encoded as a base-ten string with exact precision.</summary>
[JsonConverter(typeof(DecimalStringJsonConverter))]
public readonly record struct DecimalString(decimal Value)
{
    internal const string Pattern = @"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?![\s\S])";
    const string Format = "0.############################";

    /// <summary>Parses an invariant decimal spelling without rounding.</summary>
    public static bool TryParse(string? text, out DecimalString value) => TryParse(text.AsSpan(), out value);

    static bool TryParse(ReadOnlySpan<char> text, out DecimalString value)
    {
        value = default;
        var negative = !text.IsEmpty && text[0] == '-';
        var magnitude = negative ? text[1..] : text;
        var point = magnitude.IndexOf('.');
        var whole = point < 0 ? magnitude : magnitude[..point];

        if (whole.IsEmpty || whole.Length > 1 && whole[0] == '0' || whole.ContainsAnyExceptInRange('0', '9'))
            return false;


        var exact = magnitude;

        if (point >= 0)
        {
            var fraction = magnitude[(point + 1)..];

            if (fraction.IsEmpty || fraction.ContainsAnyExceptInRange('0', '9'))
                return false;

            exact = magnitude.TrimEnd('0').TrimEnd('.');
        }


        if (!decimal.TryParse(exact, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
            return false;

        // decimal.TryParse may successfully round an input. Compare its exact fixed-point representation.
        Span<char> formatted = stackalloc char[DecimalStringMaxFormattedLength];

        if (!parsed.TryFormat(formatted, out var written, Format, CultureInfo.InvariantCulture) ||
            !exact.SequenceEqual(formatted[..written]))
            return false;


        value = new DecimalString(negative ? -parsed : parsed);
        return true;
    }

    // The longest magnitude is 29 coefficient digits plus a decimal point.
    const int DecimalStringMaxFormattedLength = 30;

    /// <summary>Returns the invariant fixed-point spelling without insignificant fractional zeros.</summary>
    public override string ToString() => Value.ToString(Format, CultureInfo.InvariantCulture);
}
