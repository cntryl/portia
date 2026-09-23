using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

// Syntax predicates run on every node of every edited file, and whatever they accept is bound again on every
// compilation, so each one admits only syntax that could possibly matter.
static class SyntaxFilters
{
    // A reference to a named type written in a type position: a declaration, signature, generic argument,
    // construction, cast, pattern, or typeof. Identifiers in ordinary expressions are TypeSyntax too, but they
    // name values, and binding every one of them was the dominant cost of event discovery. Wrappers such as
    // arrays, nullables, and tuples are skipped because the named types inside them are visited themselves,
    // and so are the parts of a qualified name, which is visited whole.
    public static bool IsTypeReference(SyntaxNode node) => node switch
    {
        PredefinedTypeSyntax or ArrayTypeSyntax or NullableTypeSyntax or PointerTypeSyntax or TupleTypeSyntax
            or RefTypeSyntax or ScopedTypeSyntax or FunctionPointerTypeSyntax or OmittedTypeArgumentSyntax => false,
        NameSyntax { Parent: QualifiedNameSyntax or AliasQualifiedNameSyntax } => false,
        // An implicitly typed local names no type; whatever it holds is written at its construction or signature.
        IdentifierNameSyntax { IsVar: true } => false,
        TypeSyntax type => SyntaxFacts.IsInTypeOnlyContext(type) || NamesTypeInExpression(type),
        _ => false
    };

    // Places the parser reads a type as an expression: a type pattern in a switch (`case E:`, `E => ...`) and the
    // receiver of a static member (`E.Create()`). Each still references the type itself.
    static bool NamesTypeInExpression(TypeSyntax type) => type is NameSyntax && type.Parent switch
    {
        ConstantPatternSyntax or CaseSwitchLabelSyntax => true,
        MemberAccessExpressionSyntax access => access.Expression == type,
        _ => false
    };

    // A class or record that could derive from a Portia base type or implement one of its interfaces.
    public static bool HasBaseList(SyntaxNode node) =>
        node is ClassDeclarationSyntax { BaseList: not null } or RecordDeclarationSyntax { BaseList: not null };
}

// The simple names of every public domain event a referenced Portia assembly declares. A type reference whose
// name is not among them cannot be an external event, so it is never bound. Metadata assembly symbols are
// shared across the compilations of one project while its references are unchanged, so each assembly is
// walked once, not on every edit.
static class ReferencedEventNames
{
    static readonly ConditionalWeakTable<IAssemblySymbol, HashSet<string>> ByAssembly = new();
    static readonly ConditionalWeakTable<Compilation, Func<string, bool>> ByCompilation = new();

    public static bool MayName(GeneratorSyntaxContext context)
    {
        var name = context.Node switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax qualified => qualified.Name.Identifier.ValueText,
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => null
        };
        return name is null || ByCompilation.GetValue(context.SemanticModel.Compilation, Create)(name);
    }

    static Func<string, bool> Create(Compilation compilation)
    {
        // An alias can give an event any name, so a compilation that declares one binds every reference.
        if (compilation.SyntaxTrees.Any(tree => tree.GetRoot() is CompilationUnitSyntax root
                                                && (root.Usings.Any(HasAlias)
                                                    || root.DescendantNodes(node => node is CompilationUnitSyntax
                                                            or BaseNamespaceDeclarationSyntax)
                                                        .OfType<UsingDirectiveSyntax>().Any(HasAlias))))
        {
            return static _ => true;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
            names.UnionWith(ByAssembly.GetValue(assembly, EventNames));
        return names.Contains;

        static bool HasAlias(UsingDirectiveSyntax directive) => directive.Alias is not null;
    }

    static HashSet<string> EventNames(IAssemblySymbol assembly)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (!assembly.Modules.Any(module => module.ReferencedAssemblySymbols.Any(reference =>
                reference.Name == "Portia.Abstractions")))
        {
            return names;
        }

        var pending = new Stack<INamespaceOrTypeSymbol>();
        pending.Push(assembly.GlobalNamespace);
        while (pending.Count > 0)
        {
            foreach (var member in pending.Pop().GetMembers())
            {
                if (member is INamespaceOrTypeSymbol container)
                    pending.Push(container);
                if (member is INamedTypeSymbol { DeclaredAccessibility: Accessibility.Public } type
                    && DerivesFromDomainEvent(type))
                {
                    _ = names.Add(type.Name);
                }
            }
        }

        return names;
    }

    static bool DerivesFromDomainEvent(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.Name == "DomainEvent" && current.ContainingNamespace?.ToDisplayString() == "Cntryl.Portia")
                return true;
        }

        return false;
    }
}
