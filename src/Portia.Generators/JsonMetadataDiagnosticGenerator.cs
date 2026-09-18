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
    public void Initialize(IncrementalGeneratorInitializationContext context) =>
        context.RegisterSourceOutput(context.CompilationProvider,
            static (output, compilation) => Analyze(output, compilation));

    static void Analyze(SourceProductionContext output, Compilation compilation)
    {
        if (compilation.GetTypeByMetadataName("Cntryl.Portia.PortiaJsonContextAttribute") is null)
        {
            return;
        }

        var covered = ContextRoots(compilation).ToImmutableHashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var required = new Dictionary<ITypeSymbol, Location>(SymbolEqualityComparer.Default);
        var streamType = compilation.GetTypeByMetadataName("System.IO.Stream");

        foreach (var type in Types(compilation.Assembly.GlobalNamespace))
        {
            if (IsConcreteDomainEvent(type) && HasDiscriminator(type))
            {
                Add(type, type.Locations.FirstOrDefault());
            }
        }

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var syntaxName = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    GenericNameSyntax generic => generic.Identifier.ValueText,
                    MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
                    _ => string.Empty
                };
                if (!CandidateMethods.Contains(syntaxName))
                    continue;
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
                {
                    continue;
                }

                var name = method.Name;
                var registration = IsPortiaRegistration(method);
                var endpointMapping = IsPortiaEndpointMapping(method);
                if ((registration && name is "AddEvent" or "RegisterDynamicRequest") || endpointMapping)
                {
                    foreach (var argument in method.TypeArguments)
                        Add(argument, invocation.GetLocation());
                }
                else if (registration && name == "AddRequestHandler" &&
                         method.TypeArguments.FirstOrDefault() is INamedTypeSymbol handler)
                {
                    foreach (var iface in handler.AllInterfaces.Where(IsHandlerInterface))
                    {
                        Add(iface.TypeArguments[0], invocation.GetLocation());
                        if (iface.TypeArguments.Length == 2)
                        {
                            Add(iface.TypeArguments[1], invocation.GetLocation());
                        }
                    }
                }

                if (name == "AddMcpTool" && IsPortiaMcpRegistration(method)
                                         && method.TypeArguments.FirstOrDefault() is INamedTypeSymbol mcpRequest)
                {
                    Add(mcpRequest, invocation.GetLocation());
                    var requestContract = mcpRequest.AllInterfaces.FirstOrDefault(iface =>
                        iface.OriginalDefinition.ToDisplayString() == "Cntryl.Portia.IRequest<TOut>");
                    if (requestContract is not null)
                        Add(requestContract.TypeArguments[0], invocation.GetLocation());
                }

                if (IsPortiaDispatch(method))
                {
                    var requestParameter = method.Parameters.FirstOrDefault(parameter => parameter.Name == "request");
                    var requestArgument = requestParameter is null
                        ? null
                        : invocation.ArgumentList.Arguments.FirstOrDefault(argument =>
                              argument.NameColon?.Name.Identifier.ValueText == "request")
                          ?? invocation.ArgumentList.Arguments.ElementAtOrDefault(requestParameter.Ordinal);
                    if (requestArgument is not null && model.GetTypeInfo(requestArgument.Expression).Type is
                        { } dispatchedType)
                    {
                        Add(dispatchedType, invocation.GetLocation());
                    }

                    foreach (var argument in method.TypeArguments)
                        Add(argument, invocation.GetLocation());
                }

                if (endpointMapping &&
                    method.TypeArguments.FirstOrDefault() is INamedTypeSymbol request)
                {
                    var mutating = name is "MapPortiaPost" or "MapPortiaPut" or "MapPortiaPatch";
                    var operation = model.GetOperation(invocation) as IInvocationOperation;
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
                            {
                                Add(UnwrapNullable(parameter.Type), invocation.GetLocation());
                            }
                        }
                    }
                }

                if (name is "Accepts" or "Parameter" or "Produces" &&
                    method.ContainingType.OriginalDefinition.ToDisplayString() ==
                    "Cntryl.Portia.PortiaEndpointConfigurationBase<TRequest, TConfiguration>")
                {
                    foreach (var argument in method.TypeArguments)
                    {
                        if (name is "Accepts" or "Produces" && IsOrDerivesFrom(argument, streamType))
                            continue;
                        Add(argument, invocation.GetLocation());
                    }
                }
            }
        }

        foreach (var pair in required.Where(pair => !covered.Contains(pair.Key))
                     .OrderBy(pair => pair.Key.ToDisplayString(), StringComparer.Ordinal))
        {
            output.ReportDiagnostic(Diagnostic.Create(MissingMetadata, pair.Value,
                ImmutableDictionary<string, string?>.Empty
                    .Add("TypeName", pair.Key.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                    .Add("Namespace", pair.Key.ContainingNamespace is { IsGlobalNamespace: false } space
                        ? space.ToDisplayString()
                        : string.Empty),
                pair.Key.ToDisplayString()));
        }

        void Add(ITypeSymbol type, Location? location)
        {
            if (type.TypeKind is TypeKind.Error or TypeKind.TypeParameter ||
                type.SpecialType == SpecialType.System_Void)
            {
                return;
            }

            if (!required.ContainsKey(type))
            {
                required.Add(type, location ?? Location.None);
            }
        }
    }

    static IEnumerable<ITypeSymbol> ContextRoots(Compilation compilation)
    {
        foreach (var context in Types(compilation.Assembly.GlobalNamespace).Where(IsPortiaContext))
        {
            foreach (var attribute in context.GetAttributes().Where(a =>
                         a.AttributeClass?.ToDisplayString() ==
                         "System.Text.Json.Serialization.JsonSerializableAttribute"))
            {
                if (attribute.ConstructorArguments.FirstOrDefault().Value is ITypeSymbol type)
                {
                    yield return type;
                }
            }
        }


        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            foreach (var attribute in assembly.GetAttributes().Where(a =>
                         a.AttributeClass?.ToDisplayString() == "Cntryl.Portia.PortiaJsonRootAttribute"))
            {
                if (attribute.ConstructorArguments.FirstOrDefault().Value is ITypeSymbol type)
                    yield return type;
            }
        }
    }

    static bool IsPortiaContext(INamedTypeSymbol type) => type.GetAttributes().Any(a =>
        a.AttributeClass?.ToDisplayString() == "Cntryl.Portia.PortiaJsonContextAttribute");

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

    static bool IsConcreteDomainEvent(INamedTypeSymbol type)
    {
        if (type.IsAbstract || type.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected)
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

    static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol scope)
    {
        foreach (var type in scope.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in Nested(type))
                yield return nested;
        }

        foreach (var child in scope.GetNamespaceMembers())
        {
            foreach (var type in Types(child))
                yield return type;
        }
    }

    static IEnumerable<INamedTypeSymbol> Nested(INamedTypeSymbol owner)
    {
        foreach (var type in owner.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in Nested(type))
                yield return nested;
        }
    }
}
