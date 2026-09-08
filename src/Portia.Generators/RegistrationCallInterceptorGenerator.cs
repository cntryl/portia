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

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var calls = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        Name: GenericNameSyntax { Identifier.ValueText: "AddRequestHandler" or "AddRequestAuthorizer" or "RegisterDynamicRequest" }
                            or IdentifierNameSyntax { Identifier.ValueText: "AddPortia" },
                    },
                },
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

    static Call? Analyze(GeneratorSyntaxContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method
            || context.SemanticModel.GetInterceptableLocation(invocation) is not { } location)
        {
            return null;
        }

        if (method.Name == "AddPortia"
            && method.ContainingType.ToDisplayString() == "Cntryl.Portia.PortiaApplicationServiceCollectionExtensions")
        {
            var applicationRole = method.Parameters.Length == 0 ? "empty application" : "application";
            return new Call(location, applicationRole, EventRegistrations(context.SemanticModel.Compilation), null, invocation.GetLocation());
        }
        if (method.ContainingType.ToDisplayString() != "Cntryl.Portia.PortiaBuilder" || method.TypeArguments.Length != 1)
            return null;

        var role = method.Name switch
        {
            "AddRequestHandler" => "handler",
            "AddRequestAuthorizer" => "authorizer",
            _ => "dynamic request",
        };
        if (method.TypeArguments[0] is not INamedTypeSymbol type)
            return new Call(location, role, null, "use a concrete named type", invocation.GetLocation());

        var interfaces = type.AllInterfaces.Where(i => i.OriginalDefinition.ContainingNamespace.ToDisplayString() == "Cntryl.Portia").ToArray();
        var selected = interfaces.Where(i => role switch
        {
            "handler" => i.OriginalDefinition.MetadataName is "IRequestHandler`1" or "IRequestHandler`2" or "IStreamRequestHandler`2",
            "authorizer" => i.OriginalDefinition.MetadataName == "IRequestAuthorizer`1",
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
        foreach (var iface in selected)
        {
            if (role == "authorizer")
            {
                _ = body.Append("_ = builder.AddGeneratedAuthorizer(new global::Cntryl.Portia.RequestAuthorizerRegistration<")
                    .Append(Type(iface.TypeArguments[0])).Append(", ").Append(Type(type)).AppendLine(">(stage));");
                continue;
            }
            var request = iface.TypeArguments[0];
            var descriptor = iface.OriginalDefinition.MetadataName == "IStreamRequestHandler`2" ? "StreamRequestRegistration" : "RequestRegistration";
            _ = body.Append("_ = builder.AddGeneratedHandler(new global::Cntryl.Portia.").Append(descriptor).Append('<')
                .Append(Type(request)).Append(", ").Append(Type(type));
            if (iface.TypeArguments.Length == 2)
                _ = body.Append(", ").Append(Type(iface.TypeArguments[1]));
            _ = body.Append(">(").Append(Permission(request)).AppendLine("));");
            if (RequestTransportDiscovery.GetRequestTransportComponent(request) is { } transport)
                _ = body.Append("_ = builder.AddGeneratedRequest(").Append(RequestExpression(transport)).AppendLine(");");
        }
        _ = body.Append(EventRegistrations(context.SemanticModel.Compilation));
        return new Call(location, role, body.ToString(), null, invocation.GetLocation());
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
        foreach (var assembly in compilation.SourceModule.ReferencedAssemblySymbols.Prepend(compilation.Assembly))
        {
            foreach (var type in Types(assembly.GlobalNamespace).Where(type => IsDomainEvent(type, SymbolEqualityComparer.Default.Equals(assembly, compilation.Assembly)))
                .OrderBy(type => type.ToDisplayString(), StringComparer.Ordinal))
            {
                _ = source.Append("_ = builder.AddEvent<").Append(Type(type)).AppendLine(">();");
            }
        }
        return source.ToString();
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

    static string Permission(ITypeSymbol request)
    {
        if (request.GetAttributes()
            .FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == "Cntryl.Portia.RequiresPermissionAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value is not string value)
        {
            return "null";
        }

        var properties = request.GetMembers().OfType<IPropertySymbol>().ToArray();
        var expression = new StringBuilder("static typed => $\"");
        var offset = 0;
        foreach (Match match in PermissionTokenPattern.Matches(value))
        {
            _ = expression.Append(Escape(value.Substring(offset, match.Index - offset)));
            var property = properties.FirstOrDefault(candidate => string.Equals(candidate.Name, match.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            if (property is null)
                return "null"; // RequestBusGenerator reports PORTIA011 for source declarations.
            _ = expression.Append("{typed.").Append(property.Name).Append('}');
            offset = match.Index + match.Length;
        }
        return expression.Append(Escape(value.Substring(offset))).Append('"').ToString();
    }

    static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("{", "{{").Replace("}", "}}");

    static string Type(ITypeSymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    static void Generate(SourceProductionContext context, ImmutableArray<Call> calls, ImmutableArray<RequestTransportComponent> dispatchedRequests)
    {
        foreach (var call in calls)
        {
            if (call.Error is not null)
                context.ReportDiagnostic(Diagnostic.Create(InvalidRegistration, call.DiagnosticLocation, call.Role, call.Role, call.Error));
        }

        var inferred = new StringBuilder();
        foreach (var request in dispatchedRequests.GroupBy(request => request.TypeName, StringComparer.Ordinal).Select(group => group.First()))
            _ = inferred.Append("_ = builder.AddGeneratedRequest(").Append(RequestExpression(request)).AppendLine(");");

        var valid = calls.Where(c => c.Body is not null).ToArray();
        if (valid.Length == 0)
            return;
        var source = new StringBuilder().AppendLine("// <auto-generated />").AppendLine("#nullable enable")
            .AppendLine("namespace System.Runtime.CompilerServices { [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)] file sealed class InterceptsLocationAttribute : global::System.Attribute { public InterceptsLocationAttribute(int version, string data) { _ = version; _ = data; } } }")
            .AppendLine("namespace Cntryl.Portia.Generated { file static class PortiaRegistrationInterceptors {");
        for (var i = 0; i < valid.Length; i++)
        {
            var call = valid[i];
            _ = source.Append("[global::System.Runtime.CompilerServices.InterceptsLocation(").Append(call.Location.Version).Append(", \"").Append(call.Location.Data).AppendLine("\")]");
            _ = call.Role == "application"
                ? source.Append("public static global::Cntryl.Portia.PortiaBuilder Register").Append(i)
                    .AppendLine("(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services, global::System.Action<global::Cntryl.Portia.PortiaBuilder> configure) {")
                    .AppendLine("global::System.ArgumentNullException.ThrowIfNull(services);")
                    .AppendLine("global::System.ArgumentNullException.ThrowIfNull(configure);")
                    .AppendLine("return global::Cntryl.Portia.PortiaApplicationServiceCollectionExtensions.AddPortia(services, builder => {")
                    .Append(call.Body).Append(inferred).AppendLine("configure(builder);").AppendLine("});").AppendLine("}")
                : call.Role == "empty application"
                    ? source.Append("public static global::Cntryl.Portia.PortiaBuilder Register").Append(i)
                        .AppendLine("(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services) {")
                        .AppendLine("global::System.ArgumentNullException.ThrowIfNull(services);")
                        .AppendLine("return global::Cntryl.Portia.PortiaApplicationServiceCollectionExtensions.AddPortia(services, builder => {")
                        .Append(call.Body).Append(inferred).AppendLine("});").AppendLine("}")
                : source.Append("public static global::Cntryl.Portia.PortiaBuilder Register").Append(i)
                    .Append(call.Role == "authorizer"
                        ? "(this global::Cntryl.Portia.PortiaBuilder builder, global::Cntryl.Portia.AuthorizationStage stage) {\n"
                        : "(this global::Cntryl.Portia.PortiaBuilder builder) {\n")
                    .AppendLine("global::System.ArgumentNullException.ThrowIfNull(builder);").Append(call.Body)
                    .AppendLine("return builder;").AppendLine("}");
        }
        _ = source.AppendLine("} }");
        context.AddSource("PortiaGeneratedRegistrationInterceptors.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    sealed class Call(InterceptableLocation location, string role, string? body, string? error, Location diagnosticLocation)
    {
        public InterceptableLocation Location { get; } = location;
        public string Role { get; } = role;
        public string? Body { get; } = body;
        public string? Error { get; } = error;
        public Location DiagnosticLocation { get; } = diagnosticLocation;
    }
}
