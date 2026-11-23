using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace Cntryl.Portia;

// One registry per composed application; descriptors and their generated accessors are cached.
sealed class PortiaContractMetadataRegistry(
    IReadOnlyList<Func<Type, PortiaContractMetadata?>> providers,
    IReadOnlyList<PortiaContractExample>? examples = null)
{
    PortiaContractExamples? _examples;

    internal PortiaContractExamples Examples
    {
        get
        {
            lock (_gate)
                return _examples ??= new(examples ?? [], this);
        }
    }

    readonly Lock _gate = new();
    readonly Dictionary<Type, PortiaContractMetadata?> _cache = [];

    internal PortiaContractMetadata? Type(Type type)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(type, out var found))
                return found;

            var actual = Nullable.GetUnderlyingType(type) ?? type;
            PortiaContractMetadata? metadata = null;

            foreach (var provider in providers)
            {
                var candidate = provider(actual);
                metadata ??= candidate;

                if (candidate?.IsSourceDefinition == true)
                {
                    metadata = candidate;
                    break;
                }
            }



            if (metadata is not null)
                Validate(metadata);

            _cache.Add(type, metadata);
            return metadata;
        }
    }

    internal PortiaContractMemberMetadata? Member(Type type, string name, System.Text.Json.JsonNamingPolicy? policy) =>
        Type(type)?.Members.SingleOrDefault(member => member.Name == name)
        ?? Type(type)?.Members
            .SingleOrDefault(member => string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? WireMember(type, name, policy);

    internal PortiaContractMemberMetadata?
        WireMember(Type type, string name, System.Text.Json.JsonNamingPolicy? policy) =>
        Type(type)?.Members.SingleOrDefault(member =>
            (member.JsonName ?? policy?.ConvertName(member.Name) ?? member.Name) == name);

    static void Validate(PortiaContractMetadata metadata)
    {
        foreach (var member in metadata.Members)
        {
            var identity = metadata.Type + "." + member.Name;

            if (member.HasValidation && member.GetValue is null)
                throw new InvalidOperationException(
                    $"Validation on '{identity}' has no generated value accessor. Use an accessible getter.");

            if (member.HasDefault && member.HasBindingDefault &&
                !Equals(member.DefaultValue, member.BindingDefaultValue))
                throw new InvalidOperationException(
                    $"DefaultValue on '{identity}' contradicts its constructor default.");

            foreach (var rule in member.Rules)
            {
                if (rule.ErrorMessageResourceType is not null)
                    throw new InvalidOperationException(
                        $"Resource-backed validation on '{identity}' requires a generated resource accessor. Use a literal ErrorMessage.");

                if (rule is RangeAttribute { Minimum: string })
                    throw new InvalidOperationException(
                        $"Range(Type, string, string) on '{identity}' requires a typed rule. Use the numeric Range overload.");

                if (rule is MinLengthAttribute { Length: < 0 } or MaxLengthAttribute { Length: < -1 } ||
                    rule is StringLengthAttribute length && (length.MaximumLength < 0 || length.MinimumLength < 0 ||
                                                             length.MinimumLength > length.MaximumLength))
                    throw new InvalidOperationException($"Invalid length constraint on '{identity}'.");

                if (rule is (MinLengthAttribute or MaxLengthAttribute) && member.Count is null)
                    throw new InvalidOperationException(
                        $"Length validation on '{identity}' needs a string, array, ICollection<T> or IReadOnlyCollection<T>.");

                if (rule is RegularExpressionAttribute expression)
                    _ = new Regex(expression.Pattern, RegexOptions.None, TimeSpan.FromSeconds(2));

                _ = rule.FormatErrorMessage(member.Title ?? member.Name);
            }
        }
    }
}
