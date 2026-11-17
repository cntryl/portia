using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

static class ContractAttributeDiagnostics
{
    static readonly DiagnosticDescriptor Invalid = new("PORTIA031", "Invalid contract metadata",
        "Contract metadata '{0}' cannot be generated: {1}", "Portia", DiagnosticSeverity.Error, true);

    internal static void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var diagnostics = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => node is AttributeSyntax,
            static (syntax, ct) => Inspect(syntax, ct)).Where(static diagnostic => diagnostic is not null);
        var requests = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => node is TypeDeclarationSyntax { BaseList: not null },
            static (syntax, ct) => syntax.SemanticModel.GetDeclaredSymbol(syntax.Node, ct) is INamedTypeSymbol type &&
                type.AllInterfaces.Any(contract => contract.ToDisplayString() == "Cntryl.Portia.IRequestBase")
                ? JsonDescriptionEmission.ContractTypes([type]) : []).Collect();
        var json = context.SyntaxProvider.ForAttributeWithMetadataName("Cntryl.Portia.PortiaJsonContextAttribute",
            static (node, _) => node is TypeDeclarationSyntax,
            static (syntax, _) => JsonDescriptionEmission.ContractTypes(syntax.TargetSymbol.GetAttributes()
                .Where(attribute => attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializableAttribute")
                .Select(attribute => attribute.ConstructorArguments.FirstOrDefault().Value).OfType<ITypeSymbol>())).Collect();
        var owners = requests.Combine(json).Select(static (input, _) => input.Left.Concat(input.Right)
            .SelectMany(types => types).ToImmutableHashSet(StringComparer.Ordinal));
        context.RegisterSourceOutput(diagnostics.Combine(owners), static (output, input) =>
        {
            var diagnostic = input.Left!;
            if (input.Right.Contains(diagnostic.Owner))
                output.ReportDiagnostic(Diagnostic.Create(Invalid, diagnostic.Location.ToLocation(), diagnostic.Name, diagnostic.Reason));
        });
    }

    static Problem? Inspect(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var syntax = (AttributeSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(syntax, ct).Symbol is not IMethodSymbol constructor)
            return null;
        var name = constructor.ContainingType.ToDisplayString();
        if (!JsonDescriptionEmission.IsValidationAttribute(name) && name != "System.ComponentModel.DefaultValueAttribute")
            return null;
        var owner = syntax.Parent?.Parent;
        var symbol = owner is FieldDeclarationSyntax field
            ? context.SemanticModel.GetDeclaredSymbol(field.Declaration.Variables[0], ct)
            : owner is null ? null : context.SemanticModel.GetDeclaredSymbol(owner, ct);
        var attribute = symbol?.GetAttributes().FirstOrDefault(candidate => candidate.ApplicationSyntaxReference?.Span == syntax.Span);
        if (attribute is null)
            return null;
        var args = attribute.ConstructorArguments;
        string? reason = null;
        if (name is "System.ComponentModel.DataAnnotations.RangeAttribute" or "System.ComponentModel.DefaultValueAttribute" &&
            args.FirstOrDefault().Kind == TypedConstantKind.Type)
            reason = "Type-converter overloads are not NativeAOT-safe. Use a literal numeric/string overload, or an explicit typed request guard for other value types.";
        else if (name.EndsWith("MinLengthAttribute", StringComparison.Ordinal) && args.FirstOrDefault().Value is int minimum && minimum < 0)
            reason = "MinLength must be nonnegative.";
        else if (name.EndsWith("MaxLengthAttribute", StringComparison.Ordinal) && args.FirstOrDefault().Value is int maximum && maximum < -1)
            reason = "MaxLength must be nonnegative or -1 (unbounded).";
        else if (name.EndsWith("StringLengthAttribute", StringComparison.Ordinal) && args.FirstOrDefault().Value is int length)
        {
            var min = attribute.NamedArguments.FirstOrDefault(pair => pair.Key == "MinimumLength").Value.Value as int? ?? 0;
            if (length < 0 || min < 0 || min > length)
                reason = "StringLength requires 0 <= MinimumLength <= MaximumLength.";
        }
        else if (name.EndsWith("RegularExpressionAttribute", StringComparison.Ordinal) && args.FirstOrDefault().Value is string pattern)
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException error)
            {
                reason = "The .NET regular expression is invalid: " + error.Message;
            }
        }
        else if (name == "System.ComponentModel.DefaultValueAttribute" && symbol is IPropertySymbol property)
        {
            var parameter = property.ContainingType.InstanceConstructors.SelectMany(ctor => ctor.Parameters)
                .FirstOrDefault(candidate => candidate.HasExplicitDefaultValue &&
                    string.Equals(candidate.Name, property.Name, StringComparison.OrdinalIgnoreCase));
            if (parameter is not null && Convert.ToString(parameter.ExplicitDefaultValue, CultureInfo.InvariantCulture) !=
                Convert.ToString(args.FirstOrDefault().Value, CultureInfo.InvariantCulture))
                reason = "DefaultValue contradicts the known constructor default; the attribute does not initialize the property.";
        }
        if (attribute.NamedArguments.Any(pair =>
                pair.Key == "ErrorMessageResourceType" && !pair.Value.IsNull))
            reason = "Resource-backed validation requires reflection. Use a literal ErrorMessage or a typed request guard.";
        var memberType = symbol switch { IPropertySymbol propertyType => propertyType.Type, IFieldSymbol fieldType => fieldType.Type, IParameterSymbol parameterType => parameterType.Type, _ => null };
        if (JsonDescriptionEmission.IsValidationAttribute(name) && symbol is IPropertySymbol { GetMethod.DeclaredAccessibility: not Accessibility.Public })
            reason = "Value validation needs an accessible public getter; otherwise its rule cannot be enforced.";
        if (name is "System.ComponentModel.DataAnnotations.MinLengthAttribute" or "System.ComponentModel.DataAnnotations.MaxLengthAttribute" &&
            memberType is not null && JsonDescriptionEmission.CountExpression(memberType) is null)
            reason = "Length validation needs a string, array, ICollection<T> or IReadOnlyCollection<T>.";
        var containingType = symbol as INamedTypeSymbol ?? symbol?.ContainingType;
        return reason is null || containingType is null ? null : new Problem(name, reason, containingType.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), DiagnosticLocation.From(syntax.GetLocation()));
    }

    sealed record Problem(string Name, string Reason, string Owner, DiagnosticLocation Location);
}
