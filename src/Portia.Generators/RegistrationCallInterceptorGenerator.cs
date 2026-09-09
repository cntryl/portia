using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia;

/// <summary>Turns stable generic registration calls into fully typed descriptors at compile time.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class RegistrationCallInterceptorGenerator : IIncrementalGenerator
{
    static readonly Regex PermissionTokenPattern = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);
    static readonly DiagnosticDescriptor InvalidRegistration = new("PORTIA018", "Invalid component registration",
        "Cannot register '{0}' as a Portia {1}: {2}", "Portia", DiagnosticSeverity.Error, true);
    static readonly DiagnosticDescriptor UnknownPermissionToken = new(
        "PORTIA011",
        "Unknown permission token",
        "RequiresPermission on '{0}' references '{{{1}}}', which does not match a request property",
        "Portia",
        DiagnosticSeverity.Error,
        true);
    static readonly DiagnosticDescriptor NullablePermissionToken = new(
        "PORTIA013",
        "Permission token references a nullable property",
        "RequiresPermission on '{0}' references '{{{1}}}', which is nullable; use a non-nullable property",
        "Portia",
        DiagnosticSeverity.Error,
        true);
    static readonly DiagnosticDescriptor UnsupportedRegistrationCallSite = new(
        "PORTIA019",
        "Unsupported registration call site",
        "Portia cannot generate the '{0}' registration at this call site; call it directly from ordinary executable code",
        "Portia",
        DiagnosticSeverity.Error,
        true);

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var calls = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => IsRegistrationSyntax(node),
                static (ctx, _) => Analyze(ctx))
            .Where(static call => call is not null)
            .Select(static (call, _) => call!)
            .Collect();
        var dispatchedRequests = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        Name.Identifier.ValueText: "SendAsync" or "StreamAsync" or "DispatchAsync" or "DispatchStreamAsync"
                            or "EnqueueAsync" or "PublishAsync" or "ScheduleAsync",
                    },
                },
                static (ctx, _) => DispatchedRequest(ctx))
            .Where(static request => request is not null)
            .Select(static (request, _) => request!)
            .Collect();
        context.RegisterSourceOutput(calls.Combine(dispatchedRequests), static (ctx, pair) => Generate(ctx, pair.Left, pair.Right));
    }

    static bool IsRegistrationSyntax(SyntaxNode node) => node switch
    {
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: { } name } } => IsRegistrationName(name),
        MemberAccessExpressionSyntax { Name: { } name, Parent: not InvocationExpressionSyntax } => IsRegistrationName(name),
        _ => false,
    };

    static bool IsRegistrationName(SimpleNameSyntax name) => name.Identifier.ValueText is
        "AddPortia" or "AddRequestHandler" or "AddRequestAuthorizer" or "AddRequestPipelineBehavior" or "RegisterDynamicRequest" or "AddEvent";

    static Call? Analyze(GeneratorSyntaxContext context)
    {
        var syntax = context.Node;
        if (context.SemanticModel.GetSymbolInfo(syntax).Symbol is not IMethodSymbol method)
        {
            return null;
        }

        var role = RegistrationRole(method);
        if (role is null)
        {
            return null;
        }
        if (syntax is not InvocationExpressionSyntax invocation)
        {
            return new Call(null, role, null, null, syntax.GetLocation(),
                Diagnostic.Create(UnsupportedRegistrationCallSite, syntax.GetLocation(), role));
        }
        if (context.SemanticModel.GetInterceptableLocation(invocation) is not { } location)
        {
            return new Call(null, role, null, null, invocation.GetLocation(),
                Diagnostic.Create(UnsupportedRegistrationCallSite, invocation.GetLocation(), role));
        }

        if (role == "application")
        {
            return new Call(location, "application", JsonContextRegistrations(context.SemanticModel.Compilation)
                + EventRegistrations(context.SemanticModel.Compilation), null, invocation.GetLocation());
        }
        if (method.TypeArguments[0] is not INamedTypeSymbol type)
            return new Call(location, role, null, "use a concrete named type", invocation.GetLocation());

        if (role == "domain event")
        {
            var attribute = type.GetAttributes().FirstOrDefault(candidate =>
                candidate.AttributeClass?.ToDisplayString() == "Cntryl.Portia.DiscriminatorAttribute");
            return attribute is not null && attribute.ConstructorArguments.Length == 2
                && attribute.ConstructorArguments[0].Value is string name
                && attribute.ConstructorArguments[1].Value is int version
                ? new Call(location, role, $"_ = builder.AddGeneratedEvent<{Type(type)}>({version}, {RequestTransportDiscovery.FormatStringLiteral(name)});",
                    null, invocation.GetLocation())
                : new Call(location, role, null, "the event needs a valid Discriminator attribute", invocation.GetLocation());
        }

        var interfaces = type.AllInterfaces.Where(i => i.OriginalDefinition.ContainingNamespace.ToDisplayString() == "Cntryl.Portia").ToArray();
        var selected = interfaces.Where(i => role switch
        {
            "handler" => i.OriginalDefinition.MetadataName is "IRequestHandler`1" or "IRequestHandler`2" or "IStreamRequestHandler`2",
            "authorizer" => i.OriginalDefinition.MetadataName == "IRequestAuthorizer`1",
            "behavior" => i.OriginalDefinition.MetadataName is "IRequestPipelineBehavior`1" or "IRequestPipelineBehavior`2" or "IStreamRequestPipelineBehavior`2",
            "request" => false,
            _ => false,
        }).ToArray();

        if (role == "dynamic request")
        {
            var transport = RequestTransportDiscovery.GetRequestTransportComponent(type);
            return transport is null
                ? new Call(location, role, null, "the request needs RequestRoute and at least one transport marker", invocation.GetLocation())
                : new Call(location, role, RequestExpression(transport) + ";\n" + EventRegistrations(context.SemanticModel.Compilation), null, invocation.GetLocation());
        }
        if (selected.Length == 0)
            return new Call(location, role, null, $"the type does not implement a Portia {role} interface", invocation.GetLocation());

        var body = new StringBuilder();
        var permissionDiagnostic = role == "handler"
            ? selected.Select(iface => PermissionDiagnostic(iface.TypeArguments[0], invocation.GetLocation())).FirstOrDefault(diagnostic => diagnostic is not null)
            : null;
        if (permissionDiagnostic is not null)
        {
            _ = body.Append("throw new global::System.InvalidOperationException(\"")
                .Append(EscapeLiteral(permissionDiagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture)))
                .AppendLine("\");");
        }
        foreach (var iface in selected)
        {
            if (role == "authorizer")
            {
                _ = body.Append("_ = builder.AddGeneratedAuthorizer(new global::Cntryl.Portia.RequestAuthorizerRegistration<")
                    .Append(Type(iface.TypeArguments[0])).Append(", ").Append(Type(type)).AppendLine(">(stage));");
                continue;
            }
            if (role == "behavior")
            {
                var behaviorDescriptor = iface.OriginalDefinition.MetadataName == "IStreamRequestPipelineBehavior`2"
                    ? "StreamRequestPipelineBehaviorRegistration" : "RequestPipelineBehaviorRegistration";
                _ = body.Append("_ = builder.AddGeneratedBehavior(new global::Cntryl.Portia.").Append(behaviorDescriptor).Append('<')
                    .Append(Type(iface.TypeArguments[0])).Append(", ").Append(Type(type));
                if (iface.TypeArguments.Length == 2)
                    _ = body.Append(", ").Append(Type(iface.TypeArguments[1]));
                _ = body.AppendLine(">(order));");
                continue;
            }
            var request = iface.TypeArguments[0];
            var diagnostic = PermissionDiagnostic(request, invocation.GetLocation());
            var descriptor = iface.OriginalDefinition.MetadataName == "IStreamRequestHandler`2" ? "StreamRequestRegistration" : "RequestRegistration";
            _ = body.Append("_ = builder.AddGeneratedHandler(new global::Cntryl.Portia.").Append(descriptor).Append('<')
                .Append(Type(request)).Append(", ").Append(Type(type));
            if (iface.TypeArguments.Length == 2)
                _ = body.Append(", ").Append(Type(iface.TypeArguments[1]));
            _ = body.Append(">(").Append(Permission(request, diagnostic)).AppendLine("));");
            if (RequestTransportDiscovery.GetRequestTransportComponent(request) is { } transport)
                _ = body.Append("_ = builder.AddGeneratedRequest(").Append(RequestExpression(transport)).AppendLine(");");
        }
        _ = body.Append(EventRegistrations(context.SemanticModel.Compilation));
        return new Call(location, role, body.ToString(), null, invocation.GetLocation(), permissionDiagnostic);
    }

    static string? RegistrationRole(IMethodSymbol method)
    {
        var containingType = method.ContainingType.ToDisplayString();
        return method.Name == "AddPortia" && containingType == "Cntryl.Portia.PortiaApplicationServiceCollectionExtensions"
            ? "application"
            : containingType == "Cntryl.Portia.PortiaBuilder" && method.TypeArguments.Length == 1
            ? method.Name switch
            {
                "AddRequestHandler" => "handler",
                "AddRequestAuthorizer" => "authorizer",
                "AddRequestPipelineBehavior" => "behavior",
                "RegisterDynamicRequest" => "dynamic request",
                "AddEvent" => "domain event",
                _ => null,
            }
            : null;
    }

    static RequestTransportComponent? DispatchedRequest(GeneratorSyntaxContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
            || method.Parameters.Length == 0)
        {
            return null;
        }

        var owner = method.ReducedFrom?.ContainingType ?? method.ContainingType;
        var ownerName = owner.ToDisplayString();
        if (ownerName is not ("Cntryl.Portia.RequestBusExtensions"
            or "Cntryl.Portia.IRequestBus"
            or "Cntryl.Portia.IRemoteRequestSender"
            or "Cntryl.Portia.IRequestQueuePublisher"
            or "Cntryl.Portia.INoticeRequestSender"
            or "Cntryl.Portia.IRequestScheduler"
            or "Cntryl.Portia.RequestSenderContextExtensions"))
        {
            return null;
        }

        var requestParameter = method.Parameters.FirstOrDefault(parameter => parameter.Name == "request");
        if (requestParameter is null)
        {
            return null;
        }
        var argument = invocation.ArgumentList.Arguments.FirstOrDefault(argument =>
            argument.NameColon?.Name.Identifier.ValueText == "request")
            ?? invocation.ArgumentList.Arguments.ElementAtOrDefault(requestParameter.Ordinal);
        if (argument is null)
        {
            return null;
        }

        var type = context.SemanticModel.GetTypeInfo(argument.Expression).Type;
        return type is INamedTypeSymbol named ? RequestTransportDiscovery.GetRequestTransportComponent(named) : null;
    }

    static string EventRegistrations(Compilation compilation)
    {
        var source = new StringBuilder();
        var events = Types(compilation.Assembly.GlobalNamespace)
            .Where(type => IsDomainEvent(type, currentAssembly: true))
            .Concat(ReferencedEventsUsedByCompilation(compilation))
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .OrderBy(type => type.ToDisplayString(), StringComparer.Ordinal);
        foreach (var type in events)
        {
            var attribute = type.GetAttributes().FirstOrDefault(candidate =>
                candidate.AttributeClass?.ToDisplayString() == "Cntryl.Portia.DiscriminatorAttribute");
            if (attribute is null || attribute.ConstructorArguments.Length != 2)
                continue;
            var name = attribute.ConstructorArguments[0].Value as string ?? string.Empty;
            var version = attribute.ConstructorArguments[1].Value as int? ?? 0;
            _ = source.Append("_ = builder.AddGeneratedEvent<").Append(Type(type)).Append(">(")
                .Append(version).Append(", ").Append(RequestTransportDiscovery.FormatStringLiteral(name)).AppendLine(");");
        }
        return source.ToString();
    }

    static string JsonContextRegistrations(Compilation compilation)
    {
        var source = new StringBuilder();
        foreach (var type in Types(compilation.Assembly.GlobalNamespace)
            .Where(type => type.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == "Cntryl.Portia.PortiaJsonContextAttribute"))
            .OrderBy(type => type.ToDisplayString(), StringComparer.Ordinal))
        {
            _ = source.Append("_ = builder.AddGeneratedJsonContext(static options => new ")
                .Append(Type(type)).AppendLine("(options));");
        }
        return source.ToString();
    }

    static IEnumerable<INamedTypeSymbol> ReferencedEventsUsedByCompilation(Compilation compilation)
    {
        // Type syntax covers the semantic edges that make an external event part of this
        // application: handler interfaces, aggregate On<TEvent> calls, method signatures,
        // construction, casts, and explicit generic dispatch. A project reference by itself is
        // deliberately not such an edge.
        foreach (var tree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);
            foreach (var typeSyntax in tree.GetRoot().DescendantNodes().OfType<TypeSyntax>())
            {
                if (semanticModel.GetTypeInfo(typeSyntax).Type is INamedTypeSymbol type
                    && !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly)
                    && IsDomainEvent(type, currentAssembly: false))
                {
                    yield return type;
                }
            }
        }
    }

    static IEnumerable<INamedTypeSymbol> Types(INamespaceSymbol scope)
    {
        foreach (var type in scope.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in NestedTypes(type))
                yield return nested;
        }
        foreach (var child in scope.GetNamespaceMembers())
        {
            foreach (var type in Types(child))
                yield return type;
        }
    }

    static IEnumerable<INamedTypeSymbol> NestedTypes(INamedTypeSymbol owner)
    {
        foreach (var type in owner.GetTypeMembers())
        {
            yield return type;
            foreach (var nested in NestedTypes(type))
                yield return nested;
        }
    }

    static bool IsDomainEvent(INamedTypeSymbol symbol, bool currentAssembly)
    {
        if (symbol.IsAbstract || (!currentAssembly && symbol.DeclaredAccessibility != Accessibility.Public)
            || symbol.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected)
        {
            return false;
        }

        for (var current = symbol.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "Cntryl.Portia.DomainEvent")
                return true;
        }

        return false;
    }

    static string RequestExpression(RequestTransportComponent transport)
    {
        var source = new StringBuilder();
        RequestTransportRegistrationEmitter.AppendConstruction(source, transport);
        return source.ToString();
    }

    static string Permission(ITypeSymbol request, Diagnostic? diagnostic)
    {
        if (request.GetAttributes()
            .FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == "Cntryl.Portia.RequiresPermissionAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value is not string value)
        {
            return "null";
        }

        if (diagnostic is not null)
            return "null"; // The generated interceptor throws before any registration mutation.

        var properties = RequestProperties(request).ToArray();
        var expression = new StringBuilder("static typed => $\"");
        var offset = 0;
        foreach (Match match in PermissionTokenPattern.Matches(value))
        {
            _ = expression.Append(Escape(value.Substring(offset, match.Index - offset)));
            var property = properties.FirstOrDefault(candidate => string.Equals(candidate.Name, match.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            if (property is null)
                return "null"; // PermissionDiagnostic reports PORTIA011 before source emission.
            _ = expression.Append("{typed.").Append(property.Name).Append('}');
            offset = match.Index + match.Length;
        }
        return expression.Append(Escape(value.Substring(offset))).Append('"').ToString();
    }

    static Diagnostic? PermissionDiagnostic(ITypeSymbol request, Location location)
    {
        if (request.GetAttributes()
            .FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == "Cntryl.Portia.RequiresPermissionAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value is not string value)
        {
            return null;
        }

        var properties = RequestProperties(request).ToArray();
        foreach (Match match in PermissionTokenPattern.Matches(value))
        {
            var token = match.Groups[1].Value;
            var property = properties.FirstOrDefault(candidate => string.Equals(candidate.Name, token, StringComparison.OrdinalIgnoreCase));
            if (property is null)
                return Diagnostic.Create(UnknownPermissionToken, location, Type(request), token);
            if (property.NullableAnnotation == NullableAnnotation.Annotated
                || property.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                return Diagnostic.Create(NullablePermissionToken, location, Type(request), token);
            }
        }
        return null;
    }

    static IEnumerable<IPropertySymbol> RequestProperties(ITypeSymbol request)
    {
        for (var type = request as INamedTypeSymbol; type is not null; type = type.BaseType)
        {
            foreach (var property in type.GetMembers().OfType<IPropertySymbol>().Where(property => !property.IsStatic))
                yield return property;
        }
    }

    static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("{", "{{").Replace("}", "}}");

    static string EscapeLiteral(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    static string Type(ITypeSymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    static void Generate(SourceProductionContext context, ImmutableArray<Call> calls, ImmutableArray<RequestTransportComponent> dispatchedRequests)
    {
        foreach (var call in calls)
        {
            if (call.Diagnostic is not null)
                context.ReportDiagnostic(call.Diagnostic);
            if (call.Error is not null)
                context.ReportDiagnostic(Diagnostic.Create(InvalidRegistration, call.DiagnosticLocation, call.Role, call.Role, call.Error));
        }

        var inferred = new StringBuilder();
        foreach (var request in dispatchedRequests.GroupBy(request => request.TypeName, StringComparer.Ordinal).Select(group => group.First()))
            _ = inferred.Append("_ = builder.AddGeneratedRequest(").Append(RequestExpression(request)).AppendLine(");");

        var valid = calls.Where(c => c.Body is not null && c.Location is not null).ToArray();
        if (valid.Length == 0)
            return;
        var source = new StringBuilder().AppendLine("// <auto-generated />").AppendLine("#nullable enable")
            .AppendLine("namespace System.Runtime.CompilerServices { [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)] file sealed class InterceptsLocationAttribute : global::System.Attribute { public InterceptsLocationAttribute(int version, string data) { _ = version; _ = data; } } }")
            .AppendLine("namespace Cntryl.Portia.Generated { file static class PortiaRegistrationInterceptors {");
        for (var i = 0; i < valid.Length; i++)
        {
            var call = valid[i];
            _ = source.Append("[global::System.Runtime.CompilerServices.InterceptsLocation(").Append(call.Location!.Version).Append(", \"").Append(call.Location.Data).AppendLine("\")]");
            _ = call.Role == "application"
                ? source.Append("public static global::Cntryl.Portia.PortiaBuilder Register").Append(i)
                    .AppendLine("(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services) {")
                    .AppendLine("global::System.ArgumentNullException.ThrowIfNull(services);")
                    .AppendLine("var builder = global::Cntryl.Portia.PortiaApplicationServiceCollectionExtensions.AddPortia(services);")
                    .Append(call.Body).Append(inferred).AppendLine("return builder;").AppendLine("}")
                : source.Append("public static global::Cntryl.Portia.PortiaBuilder Register").Append(i)
                    .Append(call.Role switch
                    {
                        "authorizer" => "(this global::Cntryl.Portia.PortiaBuilder builder, global::Cntryl.Portia.AuthorizationStage stage) {\n",
                        "behavior" => "(this global::Cntryl.Portia.PortiaBuilder builder, int order) {\n",
                        _ => "(this global::Cntryl.Portia.PortiaBuilder builder) {\n",
                    })
                    .AppendLine("global::System.ArgumentNullException.ThrowIfNull(builder);").Append(call.Body)
                    .AppendLine("return builder;").AppendLine("}");
        }
        _ = source.AppendLine("} }");
        context.AddSource("PortiaGeneratedRegistrationInterceptors.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    sealed class Call(
        InterceptableLocation? location,
        string role,
        string? body,
        string? error,
        Location diagnosticLocation,
        Diagnostic? diagnostic = null)
    {
        public InterceptableLocation? Location { get; } = location;
        public string Role { get; } = role;
        public string? Body { get; } = body;
        public string? Error { get; } = error;
        public Location DiagnosticLocation { get; } = diagnosticLocation;
        public Diagnostic? Diagnostic { get; } = diagnostic;
    }
}
