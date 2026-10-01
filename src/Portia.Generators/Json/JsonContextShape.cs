using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

// Shared by metadata producers, registration consumers and code-fix target selection.
static class JsonContextShape
{
    public static string? UnsupportedReason(INamedTypeSymbol symbol, bool allowUnrootedGeneratedContext = false)
    {
        for (var current = symbol; current is not null; current = current.ContainingType)
        {
            if (current.IsGenericType)
                return "generic contexts and enclosing types are unsupported; use a non-generic context";
            if (current.IsFileLocal || current.DeclaredAccessibility is Accessibility.Private
                or Accessibility.Protected or Accessibility.ProtectedAndInternal)
                return $"'{current.ToDisplayString()}' is not visible to generated code; make it internal or public";
        }
        if (symbol.TypeKind != TypeKind.Class || symbol.IsAbstract)
            return "use a concrete class derived from JsonSerializerContext";
        var derived = false;
        for (var ancestor = symbol.BaseType; ancestor is not null; ancestor = ancestor.BaseType)
            if (ancestor.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializerContext")
            { derived = true; break; }
        if (!derived)
            return "the context must derive from System.Text.Json.Serialization.JsonSerializerContext";
        var requiredMembers = false;
        for (var ancestor = symbol; ancestor is not null; ancestor = ancestor.BaseType)
            requiredMembers |= ancestor.GetMembers().Any(member => member is IPropertySymbol { IsRequired: true }
                or IFieldSymbol { IsRequired: true });
        var constructors = symbol.InstanceConstructors.Where(ctor => ctor.Parameters.Length >= 1
            && ctor.Parameters[0].RefKind is RefKind.None or RefKind.In
            && ctor.Parameters[0].Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString() == "System.Text.Json.JsonSerializerOptions"
            && ctor.Parameters.Skip(1).All(parameter => parameter.IsOptional || parameter.IsParams)
            && ctor.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal)
            .ToArray();
        if (constructors.Length != 0)
        {
            var exact = constructors.Where(ctor => ctor.Parameters.Length == 1).ToArray();
            var applicable = exact.Length != 0 ? exact : constructors;
            if (applicable.Length != 1)
                return "constructor overloads are ambiguous for JsonSerializerOptions; provide one accessible options constructor";
            if (requiredMembers && !applicable[0].GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString()
                    == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute"))
                return "the options constructor must set required members; use SetsRequiredMembers after initializing them";
            return null;
        }
        if (symbol.InstanceConstructors.Any(ctor => !ctor.IsImplicitlyDeclared && ctor.Parameters.Length > 0
            && ctor.Parameters[0].Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString() == "System.Text.Json.JsonSerializerOptions"))
            return "provide an accessible constructor accepting JsonSerializerOptions without required ref/out arguments";
        // System.Text.Json generates the options constructor for an application-owned partial context.
        // Manually implemented contexts still need an actual usable constructor.
        if (symbol.BaseType?.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializerContext"
            && symbol.DeclaringSyntaxReferences.Length != 0
            && symbol.DeclaringSyntaxReferences.All(reference => reference.GetSyntax() is TypeDeclarationSyntax declaration
                && declaration.Modifiers.Any(SyntaxKind.PartialKeyword))
            && !symbol.GetMembers("GetTypeInfo").OfType<IMethodSymbol>().Any(method => method.IsOverride))
        {
            if (requiredMembers)
                return "a generated options constructor cannot initialize required members; use a manually implemented context";
            if (allowUnrootedGeneratedContext || symbol.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializableAttribute"
                && attribute.ConstructorArguments.FirstOrDefault().Value is ITypeSymbol))
                return null;
            return "add a JsonSerializable root so System.Text.Json generates the options constructor";
        }
        return "provide an accessible constructor accepting JsonSerializerOptions, or a partial context for System.Text.Json generation";
    }
}
