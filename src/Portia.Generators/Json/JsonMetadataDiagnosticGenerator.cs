using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Cntryl.Portia;

/// <summary>Ensures Portia wire roots are explicitly owned by an application JSON context.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class JsonMetadataDiagnosticGenerator : IIncrementalGenerator
{
    static readonly HashSet<string> CandidateMethods = new(StringComparer.Ordinal)
    {
        "AddEvent", "RegisterDynamicRequest", "AddRequestHandler", "AddMcpTool", "SendAsync", "StreamAsync",
        "DispatchAsync",
        "DispatchStreamAsync", "EnqueueAsync", "PublishAsync", "ScheduleAsync", "EnsureAsync", "AddRequestSchedule",
        "MapPortiaGet", "MapPortiaPost", "MapPortiaPut", "MapPortiaPatch", "MapPortiaDelete", "MapPortiaGetStream",
        "MapPortiaGetSse", "Accepts", "Parameter", "Produces"
    };

    static readonly DiagnosticDescriptor MissingMetadata = new(
        "PORTIA025", "Missing Portia JSON metadata",
        "Serializer root '{0}' must be explicitly registered with [JsonSerializable(typeof({0}))] on a [PortiaJsonContext]",
        "Portia", DiagnosticSeverity.Error, true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Each root use, event, and context is found per syntax node, so an edit re-binds only what could
        // have changed instead of re-walking the whole compilation.
        var calls = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax invocation
                                    && CandidateMethods.Contains(InvokedName(invocation)),
                static (ctx, ct) => CallRoots(ctx, ct))
            .SelectMany(static (roots, _) => roots)
            .Collect();
        var events = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => SyntaxFilters.HasBaseList(node),
                static (ctx, ct) => EventRoot(ctx, ct))
            .Where(static root => root is not null)
            .Select(static (root, _) => root!)
            .Collect();
        var declaredCoverage = context.SyntaxProvider.ForAttributeWithMetadataName(
                "Cntryl.Portia.PortiaJsonContextAttribute",
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => DeclaredCoverage((INamedTypeSymbol)ctx.TargetSymbol))
            .SelectMany(static (keys, _) => keys)
            .Collect();
        var referencedCoverage = context.CompilationProvider.Select(static (compilation, _) =>
            compilation.GetTypeByMetadataName("Cntryl.Portia.PortiaJsonContextAttribute") is null
                ? null
                : string.Join("\n", ReferencedCoverage(compilation)));

        context.RegisterSourceOutput(events.Combine(calls).Combine(declaredCoverage).Combine(referencedCoverage),
            static (output, input) => Report(output, input.Left.Left.Left.AddRange(input.Left.Left.Right),
                input.Left.Right, input.Right));
    }

    static void Report(SourceProductionContext output, ImmutableArray<RootUse> uses,
        ImmutableArray<string> declaredCoverage, string? referencedCoverage)
    {
        // No Portia JSON context attribute means the application is not built against Portia's JSON contract.
        if (referencedCoverage is null)
            return;

        var covered = new HashSet<string>(declaredCoverage, StringComparer.Ordinal);
        covered.UnionWith(referencedCoverage.Split('\n'));
        var required = new Dictionary<string, RootUse>(StringComparer.Ordinal);
        foreach (var use in uses)
        {
            if (!required.ContainsKey(use.Key))
                required.Add(use.Key, use);
        }

        foreach (var use in required.Values.Where(use => !covered.Contains(use.Key))
                     .OrderBy(use => use.Display, StringComparer.Ordinal))
        {
            output.ReportDiagnostic(Diagnostic.Create(MissingMetadata, use.Location.ToLocation(),
                ImmutableDictionary<string, string?>.Empty
                    .Add("TypeName", use.Key)
                    .Add("Namespace", use.Namespace),
                use.Display));
        }
    }

    static string InvokedName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => string.Empty
    };

    static RootUse? EventRoot(GeneratorSyntaxContext context, CancellationToken ct) =>
        context.SemanticModel.GetDeclaredSymbol(context.Node, ct) is INamedTypeSymbol type
        && IsConcreteDomainEvent(type) && HasDiscriminator(type)
            ? Use(type, type.Locations.FirstOrDefault(), context.SemanticModel.Compilation)
            : null;

    static ImmutableArray<RootUse> CallRoots(GeneratorSyntaxContext context, CancellationToken ct)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        var model = context.SemanticModel;
        if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method)
            return ImmutableArray<RootUse>.Empty;

        var roots = ImmutableArray.CreateBuilder<RootUse>();
        var location = invocation.GetLocation();
        var name = method.Name;
        var registration = IsPortiaRegistration(method);
        var endpointMapping = IsPortiaEndpointMapping(method);
        if ((registration && name is "AddEvent" or "RegisterDynamicRequest") || endpointMapping)
        {
            foreach (var argument in method.TypeArguments)
                Add(argument);
        }
        else if (registration && name == "AddRequestHandler" &&
                 method.TypeArguments.FirstOrDefault() is INamedTypeSymbol handler)
        {
            foreach (var iface in handler.AllInterfaces.Where(IsHandlerInterface))
            {
                Add(iface.TypeArguments[0]);
                if (iface.TypeArguments.Length == 2)
                    Add(iface.TypeArguments[1]);
            }
        }

        if (name == "AddMcpTool" && IsPortiaMcpRegistration(method)
                                 && method.TypeArguments.FirstOrDefault() is INamedTypeSymbol mcpRequest)
        {
            Add(mcpRequest);
            var requestContract = mcpRequest.AllInterfaces.FirstOrDefault(iface =>
                iface.OriginalDefinition.ToDisplayString() == "Cntryl.Portia.IRequest<TOut>");
            if (requestContract is not null)
                Add(requestContract.TypeArguments[0]);
        }

        if (IsPortiaDispatch(method))
        {
            var requestParameter = method.Parameters.FirstOrDefault(parameter => parameter.Name == "request");
            var requestArgument = requestParameter is null
                ? null
                : invocation.ArgumentList.Arguments.FirstOrDefault(argument =>
                      argument.NameColon?.Name.Identifier.ValueText == "request")
                  ?? invocation.ArgumentList.Arguments.ElementAtOrDefault(requestParameter.Ordinal);
            // A helper that forwards an IRequest<T> or an abstract request dispatches whatever its
            // callers pass; the concrete request is the root, found where it is constructed.
            if (requestArgument is not null && model.GetTypeInfo(requestArgument.Expression, ct).Type is
                { TypeKind: not TypeKind.Interface, IsAbstract: false } dispatchedType)
            {
                Add(dispatchedType);
            }

            foreach (var argument in method.TypeArguments)
                Add(argument);
        }

        if (endpointMapping && method.TypeArguments.FirstOrDefault() is INamedTypeSymbol request)
        {
            var mutating = name is "MapPortiaPost" or "MapPortiaPut" or "MapPortiaPatch";
            var operation = model.GetOperation(invocation, ct) as IInvocationOperation;
            var pattern = operation?.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == "pattern")
                ?.Value.ConstantValue is { HasValue: true, Value: string route }
                ? route
                : string.Empty;
            var constructor = HttpBindingShape.SinglePublicConstructor(request);
            if (mutating && constructor is not null && SupportsDefaultBinding(constructor, pattern))
            {
                foreach (var parameter in constructor.Parameters)
                {
                    if (!HttpBindingShape.IsRouteParameter(pattern, parameter.Name))
                        Add(UnwrapNullable(parameter.Type));
                }
            }
        }

        if (name is "Accepts" or "Parameter" or "Produces" &&
            method.ContainingType.OriginalDefinition.ToDisplayString() ==
            "Cntryl.Portia.PortiaEndpointConfigurationBase<TRequest, TConfiguration>")
        {
            var streamType = name is "Accepts" or "Produces"
                ? model.Compilation.GetTypeByMetadataName("System.IO.Stream")
                : null;
            foreach (var argument in method.TypeArguments)
            {
                if (name is "Accepts" or "Produces" && IsOrDerivesFrom(argument, streamType))
                    continue;
                Add(argument);
            }
        }

        return roots.ToImmutable();

        void Add(ITypeSymbol type)
        {
            if (Use(type, location, model.Compilation) is { } use)
                roots.Add(use);
        }
    }

    // An open type such as IReadOnlyList<T> cannot be named in [JsonSerializable]; its closed uses can.
    static RootUse? Use(ITypeSymbol type, Location? location, Compilation compilation)
    {
        if (type.TypeKind is TypeKind.Error || ContainsTypeParameter(type) ||
            type.SpecialType == SpecialType.System_Void)
        {
            return null;
        }

        // Only the application's own types suggest where a new context belongs; a framework root
        // such as List<T> would otherwise place the context in the framework's namespace.
        var @namespace = SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)
                         && type.ContainingNamespace is { IsGlobalNamespace: false } space
            ? space.ToDisplayString()
            : string.Empty;
        return new RootUse(Key(type), type.ToDisplayString(), @namespace, DiagnosticLocation.From(location));
    }

    static string Key(ITypeSymbol type) =>
        type.WithNullableAnnotation(NullableAnnotation.NotAnnotated)
            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    static ImmutableArray<string> DeclaredCoverage(INamedTypeSymbol context) =>
    [
        .. context.GetAttributes()
            .Where(a => a.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializableAttribute")
            .Select(attribute => attribute.ConstructorArguments.FirstOrDefault().Value)
            .OfType<ITypeSymbol>()
            .Select(Key)
    ];

    static IEnumerable<string> ReferencedCoverage(Compilation compilation)
    {
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            foreach (var attribute in assembly.GetAttributes().Where(a =>
                         a.AttributeClass?.ToDisplayString() == "Cntryl.Portia.PortiaJsonRootAttribute"))
            {
                if (attribute.ConstructorArguments.FirstOrDefault().Value is ITypeSymbol type)
                    yield return Key(type);
            }
        }
    }

    sealed record RootUse(string Key, string Display, string Namespace, DiagnosticLocation Location);

    static bool SupportsDefaultBinding(IMethodSymbol constructor, string pattern)
    {
        foreach (var parameter in constructor.Parameters)
        {
            if (!HttpBindingShape.IsSupportedParameter(parameter,
                    HttpBindingShape.IsRouteParameter(pattern, parameter.Name)))
                return false;
        }

        return true;
    }

    static bool IsOrDerivesFrom(ITypeSymbol type, INamedTypeSymbol? baseType)
    {
        if (baseType is null || type is not INamedTypeSymbol current)
            return false;
        var candidate = (INamedTypeSymbol?)current;
        for (; candidate is not null; candidate = candidate.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(candidate, baseType))
                return true;
        }

        return false;
    }

    static bool IsHandlerInterface(INamedTypeSymbol type) => type.OriginalDefinition.ToDisplayString() is
        "Cntryl.Portia.IRequestHandler<TRequest>" or "Cntryl.Portia.IRequestHandler<TRequest, TOut>"
        or "Cntryl.Portia.IStreamRequestHandler<TRequest, TOut>";

    static bool HasDiscriminator(INamedTypeSymbol type) => type.GetAttributes()
        .Any(a => a.AttributeClass?.ToDisplayString() == "Cntryl.Portia.DiscriminatorAttribute");

    static bool IsPortiaDispatch(IMethodSymbol method)
    {
        if (method.Name is not ("SendAsync" or "StreamAsync" or "DispatchAsync" or "DispatchStreamAsync"
            or "EnqueueAsync" or "PublishAsync" or "ScheduleAsync" or "EnsureAsync" or "AddRequestSchedule"))
        {
            return false;
        }

        var owner = method.ReducedFrom?.ContainingType ?? method.ContainingType;
        return owner.ToDisplayString() is "Cntryl.Portia.RequestBusExtensions" or "Cntryl.Portia.IRequestBus"
            or "Cntryl.Portia.IRemoteRequestSender" or "Cntryl.Portia.IRequestQueuePublisher"
            or "Cntryl.Portia.INoticeRequestSender" or "Cntryl.Portia.IRequestScheduler"
            or "Cntryl.Portia.RequestSenderContextExtensions" or "Cntryl.Portia.PortiaBuilder";
    }

    static bool IsPortiaRegistration(IMethodSymbol method)
    {
        var owner = method.ReducedFrom?.ContainingType ?? method.ContainingType;
        return owner.ToDisplayString() == "Cntryl.Portia.PortiaBuilder";
    }

    static bool IsPortiaMcpRegistration(IMethodSymbol method)
    {
        var owner = method.ReducedFrom?.ContainingType ?? method.ContainingType;
        return owner.ToDisplayString() == "Cntryl.Portia.PortiaMcpApplicationExtensions";
    }

    static bool IsPortiaEndpointMapping(IMethodSymbol method)
    {
        if (method.Name is not ("MapPortiaGet" or "MapPortiaPost" or "MapPortiaPut" or "MapPortiaPatch"
            or "MapPortiaDelete" or "MapPortiaGetStream" or "MapPortiaGetSse"))
        {
            return false;
        }

        var owner = method.ReducedFrom?.ContainingType ?? method.ContainingType;
        return owner.ToDisplayString() == "Cntryl.Portia.PortiaEndpointRouteBuilderExtensions";
    }

    static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
        IPointerTypeSymbol pointer => ContainsTypeParameter(pointer.PointedAtType),
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameter)
                                  || (named.ContainingType is { } containing && ContainsTypeParameter(containing)),
        _ => false
    };

    // An event generated code cannot name, such as a file-local one, cannot be rooted in a context either.
    static bool IsConcreteDomainEvent(INamedTypeSymbol type)
    {
        if (type.IsAbstract || GeneratedTypeShape.InaccessibleReason(type) is not null)
        {
            return false;
        }

        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "Cntryl.Portia.DomainEvent")
            {
                return true;
            }
        }

        return false;
    }

    static ITypeSymbol UnwrapNullable(ITypeSymbol type) => type is INamedTypeSymbol
    {
        OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
    } nullable
        ? nullable.TypeArguments[0]
        : type;
}
