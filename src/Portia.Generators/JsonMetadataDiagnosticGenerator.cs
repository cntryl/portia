using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cntryl.Portia;

/// <summary>Ensures Portia wire roots are explicitly owned by an application JSON context.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class JsonMetadataDiagnosticGenerator : IIncrementalGenerator
{
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

        foreach (var type in Types(compilation.Assembly.GlobalNamespace))
        {
            if (IsConcreteDomainEvent(type) && HasDiscriminator(type))
            {
                Add(type, type.Locations.FirstOrDefault());
            }

            foreach (var iface in type.AllInterfaces)
            {
                var definition = iface.OriginalDefinition.ToDisplayString();
                if (definition is "Cntryl.Portia.IRequestHandler<TRequest>"
                    or "Cntryl.Portia.IRequestHandler<TRequest, TOut>"
                    or "Cntryl.Portia.IStreamRequestHandler<TRequest, TOut>")
                {
                    Add(iface.TypeArguments[0], type.Locations.FirstOrDefault());
                    if (iface.TypeArguments.Length == 2)
                    {
                        Add(iface.TypeArguments[1], type.Locations.FirstOrDefault());
                    }
                }
            }
        }

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
                {
                    continue;
                }

                var name = method.Name;
                if (name is "AddEvent" or "RegisterDynamicRequest"
                    or "MapPortiaGet" or "MapPortiaPost" or "MapPortiaPut" or "MapPortiaPatch" or "MapPortiaDelete"
                    or "MapPortiaGetStream" or "MapPortiaGetSse")
                {
                    foreach (var argument in method.TypeArguments)
                        Add(argument, invocation.GetLocation());
                }
                else if (name == "AddRequestHandler" &&
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

                if (name.StartsWith("MapPortia", StringComparison.Ordinal) &&
                    method.TypeArguments.FirstOrDefault() is INamedTypeSymbol request)
                {
                    var mutating = name is "MapPortiaPost" or "MapPortiaPut" or "MapPortiaPatch";
                    if (mutating)
                    {
                        var pattern =
                            model.GetConstantValue(invocation.ArgumentList.Arguments.First().Expression)
                                .Value as string ?? string.Empty;
                        foreach (var parameter in request.Constructors
                                     .Where(c => c.DeclaredAccessibility == Accessibility.Public)
                                     .SelectMany(c => c.Parameters))
                        {
                            if (!pattern.Contains("{" + parameter.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                Add(UnwrapNullable(parameter.Type), invocation.GetLocation());
                            }
                        }
                    }
                }
            }
        }

        foreach (var pair in required.Where(pair => !covered.Contains(pair.Key))
                     .OrderBy(pair => pair.Key.ToDisplayString(), StringComparer.Ordinal))
        {
            output.ReportDiagnostic(Diagnostic.Create(MissingMetadata, pair.Value,
                ImmutableDictionary<string, string?>.Empty.Add("TypeName",
                    pair.Key.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
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
    }

    static bool IsPortiaContext(INamedTypeSymbol type) => type.GetAttributes().Any(a =>
        a.AttributeClass?.ToDisplayString() == "Cntryl.Portia.PortiaJsonContextAttribute");

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
