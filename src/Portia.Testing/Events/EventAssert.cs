using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Cntryl.Portia.Testing;

/// <summary>Compares event business payloads structurally while ignoring Portia metadata.</summary>
public static class EventAssert
{
    /// <summary>Asserts that two events have the same type and business payload.</summary>
    /// <param name="expected">The expected event.</param>
    /// <param name="actual">The observed event.</param>
    /// <exception cref="InvalidOperationException">The payloads differ.</exception>
    [RequiresUnreferencedCode("Structural event assertions require public payload properties to be retained.")]
    public static void Equal(DomainEvent expected, DomainEvent actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var difference = Difference(expected, actual, expected.GetType().Name, new HashSet<(object, object)>(PairComparer.Instance));
        if (difference is not null)
            throw new InvalidOperationException($"Event payload differs at {difference}.");
    }

    /// <summary>Asserts that two event sequences have the same payloads in the same order.</summary>
    /// <param name="expected">The expected events.</param>
    /// <param name="actual">The observed events.</param>
    /// <exception cref="InvalidOperationException">The sequences or their payloads differ.</exception>
    [RequiresUnreferencedCode("Structural event assertions require public payload properties to be retained.")]
    public static void Equal(IReadOnlyList<DomainEvent> expected, IReadOnlyList<DomainEvent> actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        if (expected.Count != actual.Count)
            throw new InvalidOperationException($"Event count differs: expected {expected.Count}, actual {actual.Count}.");
        for (var index = 0; index < expected.Count; index++)
        {
            var difference = Difference(expected[index], actual[index], $"[{index}].{expected[index]?.GetType().Name}",
                new HashSet<(object, object)>(PairComparer.Instance));
            if (difference is not null)
                throw new InvalidOperationException($"Event payload differs at {difference}.");
        }
    }

    [RequiresUnreferencedCode("Structural event assertions require public payload properties to be retained.")]
    static string? Difference(object? expected, object? actual, string path, HashSet<(object, object)> visited)
    {
        if (ReferenceEquals(expected, actual))
            return null;
        if (expected is null || actual is null || expected.GetType() != actual.GetType())
            return path;

        var type = expected.GetType();
        if (!type.IsValueType && !visited.Add((expected, actual)))
            return null;

        if (expected is IEnumerable expectedItems && expected is not string)
        {
            var left = expectedItems.Cast<object?>().ToArray();
            var right = ((IEnumerable)actual).Cast<object?>().ToArray();
            if (left.Length != right.Length)
                return $"{path}.Count";
            for (var index = 0; index < left.Length; index++)
            {
                var difference = Difference(left[index], right[index], $"{path}[{index}]", visited);
                if (difference is not null)
                    return difference;
            }

            return null;
        }

        if (type.IsPrimitive || type.IsEnum || type.Namespace == "System"
            || type.Namespace?.StartsWith("System.", StringComparison.Ordinal) == true)
            return Equals(expected, actual) ? null : path;

        var properties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0
                               && property.DeclaringType != typeof(DomainEvent))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();
        if (properties.Length == 0)
            return Equals(expected, actual) ? null : path;

        foreach (var property in properties)
        {
            var difference = Difference(property.GetValue(expected), property.GetValue(actual),
                $"{path}.{property.Name}", visited);
            if (difference is not null)
                return difference;
        }

        return null;
    }

    sealed class PairComparer : IEqualityComparer<(object, object)>
    {
        public static PairComparer Instance { get; } = new();

        public bool Equals((object, object) x, (object, object) y) =>
            ReferenceEquals(x.Item1, y.Item1) && ReferenceEquals(x.Item2, y.Item2);

        public int GetHashCode((object, object) pair) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(pair.Item1), RuntimeHelpers.GetHashCode(pair.Item2));
    }
}
