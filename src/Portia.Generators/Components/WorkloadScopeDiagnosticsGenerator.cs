using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

/// <summary>
///     Reports a projector or reactor registered under a workload scope that its declared stream pattern
///     cannot serve. The host rejects the same mismatch at startup; this reports it at the registration when
///     the component writes <c>EventStreamPattern.ForTenant(...)</c> or <c>ForPattern(...)</c> into its base
///     and both are in one compilation. A pattern chosen at runtime is left to the host.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class WorkloadScopeDiagnosticsGenerator : IIncrementalGenerator
{
    const string PatternMetadataName = "Cntryl.Portia.EventStreamPattern";

    static readonly DiagnosticDescriptor ScopeMismatch = new("PORTIA031",
        "Workload scope contradicts the declared stream pattern",
        "'{0}' is registered as workload '{1}' with WorkloadScope.{2}, but declares EventStreamPattern.{3}(...); use EventStreamPattern.{4}(...) for this scope",
        "Portia", DiagnosticSeverity.Error, true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var patterns = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (syntaxContext, ct) => ReadPattern(syntaxContext, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();
        var registrations = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        Name: GenericNameSyntax { Identifier.ValueText: "AddProjector" or "AddReactor" }
                    }
                },
                static (syntaxContext, ct) => Registration(syntaxContext, ct))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();
        context.RegisterSourceOutput(registrations.Combine(patterns),
            static (output, input) => Report(output, input.Left, input.Right));
    }

    static void Report(SourceProductionContext output, ImmutableArray<WorkloadRegistration> registrations,
        ImmutableArray<DeclaredPattern> patterns)
    {
        if (registrations.IsEmpty)
            return;
        var declared = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var pattern in patterns)
        {
            if (!declared.ContainsKey(pattern.TypeName))
                declared.Add(pattern.TypeName, pattern.TenantTemplate);
        }

        foreach (var registration in registrations)
        {
            if (!declared.TryGetValue(registration.TypeName, out var tenantTemplate)
                || tenantTemplate == (registration.Scope == "PerTenant"))
            {
                continue;
            }

            output.ReportDiagnostic(Diagnostic.Create(ScopeMismatch, registration.Location.ToLocation(),
                registration.TypeName, registration.Name, registration.Scope,
                tenantTemplate ? "ForTenant" : "ForPattern", tenantTemplate ? "ForPattern" : "ForTenant"));
        }
    }

    static WorkloadRegistration? Registration(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method
            || method.ContainingType.ToDisplayString() != "Cntryl.Portia.PortiaBuilder"
            || method.TypeArguments.Length != 1
            || method.TypeArguments[0] is not INamedTypeSymbol component)
        {
            return null;
        }

        var scopeIndex = IndexOf(method.Parameters,
            parameter => parameter.Type.ToDisplayString() == "Cntryl.Portia.WorkloadScope");
        if (scopeIndex < 0 || Argument(invocation.ArgumentList, method.Parameters, scopeIndex) is not { } scopeArgument
            || context.SemanticModel.GetConstantValue(scopeArgument.Expression, ct) is not { HasValue: true } scopeValue
            || method.Parameters[scopeIndex].Type.GetMembers().OfType<IFieldSymbol>()
                .FirstOrDefault(field => field.HasConstantValue && Equals(field.ConstantValue, scopeValue.Value))
                is not { } scope)
        {
            return null;
        }

        var nameIndex = IndexOf(method.Parameters, parameter => parameter.Name == "name");
        var name = nameIndex >= 0
                   && Argument(invocation.ArgumentList, method.Parameters, nameIndex) is { } nameArgument
                   && context.SemanticModel.GetConstantValue(nameArgument.Expression, ct).Value is string literal
            ? literal
            : component.Name;
        return new WorkloadRegistration(component.ToDisplayString(), name, scope.Name,
            DiagnosticLocation.From(invocation.GetLocation()));
    }

    static DeclaredPattern? ReadPattern(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var declaration = (ClassDeclarationSyntax)context.Node;
        if (context.SemanticModel.GetDeclaredSymbol(declaration, ct) is not { IsAbstract: false } symbol
            || !(InheritsFrom(symbol, "Cntryl.Portia.Projector") || InheritsFrom(symbol, "Cntryl.Portia.Reactor")))
        {
            return null;
        }

        return TenantTemplate(symbol, context.SemanticModel.Compilation, ct) is { } tenantTemplate
            ? new DeclaredPattern(symbol.ToDisplayString(), tenantTemplate)
            : null;
    }

    // The pattern is whatever the nearest source declaration hands to a base constructor that takes one. A
    // base declared in metadata ends the walk; a base that merely forwards a caller's pattern is unknown.
    static bool? TenantTemplate(INamedTypeSymbol symbol, Compilation compilation, CancellationToken ct)
    {
        for (var type = symbol; type is not null && !type.DeclaringSyntaxReferences.IsEmpty; type = type.BaseType)
        {
            var evidence = Evidence.None;
            foreach (var reference in type.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax(ct) is ClassDeclarationSyntax part)
                    evidence = Merge(evidence, PartEvidence(part, compilation.GetSemanticModel(part.SyntaxTree), ct));
            }

            switch (evidence)
            {
                case Evidence.Tenant:
                    return true;
                case Evidence.Pattern:
                    return false;
                case Evidence.Unknown:
                    return null;
            }
        }

        return null;
    }

    static Evidence PartEvidence(ClassDeclarationSyntax part, SemanticModel model, CancellationToken ct)
    {
        var evidence = Evidence.None;
        foreach (var baseType in part.BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>() ?? [])
        {
            evidence = Merge(evidence, ArgumentEvidence(model.GetSymbolInfo(baseType, ct).Symbol as IMethodSymbol,
                baseType.ArgumentList, model, ct));
        }

        foreach (var constructor in part.Members.OfType<ConstructorDeclarationSyntax>())
        {
            if (constructor.Initializer is { } initializer && initializer.IsKind(SyntaxKind.BaseConstructorInitializer))
            {
                evidence = Merge(evidence, ArgumentEvidence(model.GetSymbolInfo(initializer, ct).Symbol as IMethodSymbol,
                    initializer.ArgumentList, model, ct));
            }
        }

        return evidence;
    }

    static Evidence ArgumentEvidence(IMethodSymbol? constructor, ArgumentListSyntax arguments, SemanticModel model,
        CancellationToken ct)
    {
        if (constructor is null)
            return Evidence.Unknown;
        var index = IndexOf(constructor.Parameters, parameter => parameter.Type.ToDisplayString() == PatternMetadataName);
        if (index < 0)
            return Evidence.None;
        if (Argument(arguments, constructor.Parameters, index) is not { } argument)
            return Evidence.Unknown;

        var expression = argument.Expression;
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        return expression is InvocationExpressionSyntax invocation
               && model.GetSymbolInfo(invocation, ct).Symbol is IMethodSymbol factory
               && factory.ContainingType.ToDisplayString() == PatternMetadataName
            ? factory.Name switch
            {
                "ForTenant" => Evidence.Tenant,
                "ForPattern" => Evidence.Pattern,
                _ => Evidence.Unknown
            }
            : Evidence.Unknown;
    }

    // Two constructors that disagree, or one whose pattern cannot be read, leave the type unknown.
    static Evidence Merge(Evidence first, Evidence second) => (first, second) switch
    {
        (Evidence.Unknown, _) or (_, Evidence.Unknown) => Evidence.Unknown,
        (Evidence.None, _) => second,
        (_, Evidence.None) => first,
        _ when first == second => first,
        _ => Evidence.Unknown
    };

    static ArgumentSyntax? Argument(ArgumentListSyntax arguments, ImmutableArray<IParameterSymbol> parameters,
        int index)
    {
        var positional = 0;
        foreach (var argument in arguments.Arguments)
        {
            if (argument.NameColon is { } named)
            {
                if (named.Name.Identifier.ValueText == parameters[index].Name)
                    return argument;
                positional++;
            }
            else if (positional++ == index)
            {
                return argument;
            }
        }

        return null;
    }

    static int IndexOf(ImmutableArray<IParameterSymbol> parameters, Func<IParameterSymbol, bool> predicate)
    {
        for (var index = 0; index < parameters.Length; index++)
        {
            if (predicate(parameters[index]))
                return index;
        }

        return -1;
    }

    static bool InheritsFrom(INamedTypeSymbol symbol, string baseTypeMetadataName)
    {
        for (var type = symbol.BaseType; type is not null; type = type.BaseType)
        {
            if (type.ToDisplayString() == baseTypeMetadataName)
                return true;
        }

        return false;
    }

    enum Evidence
    {
        None,
        Tenant,
        Pattern,
        Unknown
    }

    sealed record DeclaredPattern(string TypeName, bool TenantTemplate);

    sealed record WorkloadRegistration(string TypeName, string Name, string Scope, DiagnosticLocation Location);
}
