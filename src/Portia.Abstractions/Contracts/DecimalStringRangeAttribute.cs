using System.ComponentModel.DataAnnotations;

namespace Cntryl.Portia;

/// <summary>Validates a decimal string against exact decimal bounds.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class DecimalStringRangeAttribute : ValidationAttribute
{
    readonly decimal _lower;
    readonly decimal _upper;

    /// <summary>Creates a range using exact decimal string bounds.</summary>
    public DecimalStringRangeAttribute(string minimum, string maximum)
        : base("The field {0} is outside its decimal string range.")
    {
        if (!DecimalString.TryParse(minimum, out var lower))
            throw new ArgumentException("Expected an exact decimal string.", nameof(minimum));

        if (!DecimalString.TryParse(maximum, out var upper) || upper.Value < lower.Value)
            throw new ArgumentException("Expected an exact decimal string at or above the minimum.", nameof(maximum));

        Minimum = minimum;
        Maximum = maximum;
        _lower = lower.Value;
        _upper = upper.Value;
    }

    /// <summary>Gets the lower bound of the range as declared.</summary>
    public string Minimum { get; }

    /// <summary>Gets the upper bound of the range as declared.</summary>
    public string Maximum { get; }

    /// <summary>Whether the lower bound is excluded.</summary>
    public bool MinimumIsExclusive { get; init; }

    /// <summary>Whether the upper bound is excluded.</summary>
    public bool MaximumIsExclusive { get; init; }

    /// <inheritdoc />
    public override bool IsValid(object? value) => value is null || value is DecimalString number &&
        (MinimumIsExclusive ? number.Value > _lower : number.Value >= _lower) &&
        (MaximumIsExclusive ? number.Value < _upper : number.Value <= _upper);
}
