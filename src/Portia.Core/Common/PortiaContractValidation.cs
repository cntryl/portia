using System.ComponentModel.DataAnnotations;

namespace Cntryl.Portia;

static class PortiaContractValidation
{
    internal static RequestError? Validate(PortiaContractMetadataRegistry? contracts, object request,
        CancellationToken ct, Type? declaredType = null, PortiaContractUse use = PortiaContractUse.Both)
    {
        ct.ThrowIfCancellationRequested();
        if (contracts is null)
            return null;
        var root = contracts.Type(request.GetType()) ?? (declaredType is null ? null : contracts.Type(declaredType));
        if (root?.HasValidation != true)
            return null;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var errors = new List<string>();
        var visitedCount = 0;
        var exhausted = false;
        Visit(request, request.GetType(), string.Empty, 0, root);
        return errors.Count == 0 ? null : RequestError.Validation(string.Join("; ", errors));

        void Visit(object? value, Type? declaredType, string path, int depth, PortiaContractMetadata? known = null)
        {
            ct.ThrowIfCancellationRequested();
            if (exhausted)
                return;
            // Bound attempted work too: cycles are visited once, but repeated references and null
            // elements must not allow an enumerable to run forever without consuming its budget.
            if (++visitedCount > 100_000 || depth > 64)
            {
                exhausted = true;
                errors.Add("The request exceeds the supported validation graph size or depth.");
                return;
            }
            if (value is null)
                return;
            var metadata = known ?? contracts.Type(value.GetType()) ?? (declaredType is null ? null : contracts.Type(declaredType));
            if (metadata?.HasValidation != true || !visited.Add(value))
                return;
            foreach (var member in metadata.Members)
            {
                ct.ThrowIfCancellationRequested();
                if (!member.HasValidation || member.GetValue is null || use == PortiaContractUse.Output && !member.Output)
                    continue;
                var memberValue = member.GetValue(value);
                var memberPath = path.Length == 0 ? member.Name : path + "." + member.Name;
                foreach (var rule in member.Rules)
                    if (!IsValid(member, rule, memberValue))
                        errors.Add(memberPath + ": " + rule.FormatErrorMessage(member.Title ?? member.Name));
                if (errors.Count >= 256)
                    exhausted = true;
                Visit(memberValue, member.Type, memberPath, depth + 1);
                if (exhausted)
                    return;
            }
            if (metadata.Elements is { } elements)
            {
                var index = 0;
                foreach (var child in elements(value))
                {
                    Visit(child, metadata.ElementType, path + "[" + index++ + "]", depth + 1);
                    if (exhausted)
                        return;
                }
            }
        }
    }

    internal static bool IsValid(PortiaContractMemberMetadata member, ValidationAttribute rule, object? value)
    {
        if (value is not null && member.Count is { } count)
        {
            if (rule is MinLengthAttribute minimum)
                return count(value) >= minimum.Length;
            if (rule is MaxLengthAttribute maximum)
                return maximum.Length == -1 || count(value) <= maximum.Length;
        }
        return rule.IsValid(value);
    }
}
