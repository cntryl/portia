using System.Collections.Immutable;
using System.Globalization;
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

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var calls = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => IsRegistrationSyntax(node),
                static (ctx, _) => Analyze(ctx))
            .Where(static call => call is not null)
            .Select(static (call, _) => call!)
            .Collect()
            .WithTrackingName("PortiaRegistrationCalls");
        var dispatchedRequests = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax
                    {
                        Name.Identifier.ValueText: "SendAsync" or "StreamAsync" or "DispatchAsync"
                        or "DispatchStreamAsync"
                        or "EnqueueAsync" or "PublishAsync" or "ScheduleAsync" or "EnsureAsync"
                        or "AddRequestSchedule"
                    }
                },
                static (ctx, _) => DispatchedRequest(ctx))
            .Where(static request => request is not null)
            .Select(static (request, _) => request!)
            .Collect();
        // Events and JSON contexts are properties of the whole compilation, not of any one call
        // site. Reading them inside the per-call-site transform made every registration re-derive
        // the compilation and invalidated every call site whenever any event changed, so they get
        // their own per-node pipelines and are combined once, here.
        var jsonContexts = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, _) => JsonContextModel(ctx))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();
        var declaredEvents = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax or RecordDeclarationSyntax,
                static (ctx, _) => DeclaredEventModel(ctx))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();
        var referencedEvents = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is TypeSyntax,
                static (ctx, _) => ReferencedEventModel(ctx))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!)
            .Collect();
        var shared = jsonContexts.Combine(declaredEvents).Combine(referencedEvents)
            .Select(static (input, _) => SharedRegistrations(input.Left.Left, input.Left.Right, input.Right))
            .WithTrackingName("PortiaSharedRegistrations");
        context.RegisterSourceOutput(calls.Combine(dispatchedRequests).Combine(shared),
            static (ctx, pair) => Generate(ctx, pair.Left.Left, pair.Left.Right, pair.Right));
    }

    static bool IsRegistrationSyntax(SyntaxNode node) => node switch
    {
        InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax { Name: { } name }
        } => IsRegistrationName(name),
        MemberAccessExpressionSyntax
        {
            Name: { } name, Parent: not InvocationExpressionSyntax
        } => IsRegistrationName(name),
        _ => false
    };

    static bool IsRegistrationName(SimpleNameSyntax name) => name.Identifier.ValueText is
        "AddPortia" or "AddRequestHandler" or "AddRequestAuthorizer" or "AddRequestPipelineBehavior"
        or "RegisterDynamicRequest" or "AddEvent";

    static Call? Analyze(GeneratorSyntaxContext context)
    {
        var syntax = context.Node;
        if (context.SemanticModel.GetSymbolInfo(syntax).Symbol is not IMethodSymbol method)
            return null;

        var role = RegistrationRole(method);
        if (role == null)
            return null;

        if (syntax is not InvocationExpressionSyntax invocation)
        {
            var diagnosticLocation = DiagnosticLocation.From(syntax.GetLocation());
            return new Call(null, role, null, null, diagnosticLocation,
                new RegistrationDiagnostic(RegistrationDiagnosticKind.UnsupportedCallSite, diagnosticLocation, role));
        }

        if (context.SemanticModel.GetInterceptableLocation(invocation) is not { } location)
        {
            var diagnosticLocation = DiagnosticLocation.From(invocation.GetLocation());
            return new Call(null, role, null, null, diagnosticLocation,
                new RegistrationDiagnostic(RegistrationDiagnosticKind.UnsupportedCallSite, diagnosticLocation, role));
        }

        if (role == "application")
        {
            return new Call(InterceptableLocationModel.From(location), "application", string.Empty, null,
                DiagnosticLocation.From(invocation.GetLocation()));
        }

        if (method.TypeArguments[0] is not INamedTypeSymbol type)
            return new Call(InterceptableLocationModel.From(location), role, null, "use a concrete named type",
                DiagnosticLocation.From(invocation.GetLocation()));

        if (role == "domain event")
        {
            var attribute = type.GetAttributes().FirstOrDefault(candidate =>
                candidate.AttributeClass?.ToDisplayString() == "Cntryl.Portia.DiscriminatorAttribute");
            return attribute is not null && attribute.ConstructorArguments.Length == 2
                                         && attribute.ConstructorArguments[0].Value is string name
                                         && attribute.ConstructorArguments[1].Value is int version
                ? new Call(InterceptableLocationModel.From(location), role,
                    $"_ = builder.AddGeneratedEvent<{Type(type)}>({version}, {RequestTransportDiscovery.FormatStringLiteral(name)});",
                    null, DiagnosticLocation.From(invocation.GetLocation()))
                : new Call(InterceptableLocationModel.From(location), role, null,
                    "the event needs a valid Discriminator attribute",
                    DiagnosticLocation.From(invocation.GetLocation()));
        }

        var interfaces = type.AllInterfaces.Where(i =>
            i.OriginalDefinition.ContainingNamespace.ToDisplayString() == PortiaComponentRoles.Namespace).ToArray();
        var selected = interfaces.Where(i => role switch
        {
            "handler" => PortiaComponentRoles.Is(i, PortiaComponentRoles.Handler),
            "authorizer" => PortiaComponentRoles.Is(i, PortiaComponentRoles.Authorizer),
            "behavior" => PortiaComponentRoles.Is(i, PortiaComponentRoles.Behavior),
            _ => false
        }).ToArray();

        if (role == "dynamic request")
        {
            var transport = RequestTransportDiscovery.GetRequestTransportComponent(type);
            return transport is null
                ? new Call(InterceptableLocationModel.From(location), role, null,
                    "the request needs RequestRoute and at least one transport marker",
                    DiagnosticLocation.From(invocation.GetLocation()))
                // Constructing the descriptor is not registering it: without this the escape
                // hatch built a RequestTransportRegistration and discarded it, leaving the
                // request absent from the transport catalog it exists to put it in.
                : new Call(InterceptableLocationModel.From(location), role,
                    "_ = builder.AddGeneratedRequest(" + RequestExpression(transport) + ");\n", null,
                    DiagnosticLocation.From(invocation.GetLocation()));
        }

        if (selected.Length == 0)
        {
            return new Call(InterceptableLocationModel.From(location), role, null,
                $"the type does not implement a Portia {role} interface",
                DiagnosticLocation.From(invocation.GetLocation()));
        }

        var body = new StringBuilder();
        var permissionDiagnostic = role == "handler"
            ? selected.Select(iface => PermissionDiagnostic(iface.TypeArguments[0], invocation.GetLocation()))
                .FirstOrDefault(diagnostic => diagnostic is not null)
            : null;
        if (permissionDiagnostic is not null)
        {
            _ = body.Append("throw new global::System.InvalidOperationException(\"")
                .Append(EscapeLiteral(permissionDiagnostic.ToDiagnostic().GetMessage(CultureInfo.InvariantCulture)))
                .AppendLine("\");");
        }

        foreach (var iface in selected)
        {
            if (role == "authorizer")
            {
                _ = body.Append(
                        "_ = builder.AddGeneratedAuthorizer(new global::Cntryl.Portia.RequestAuthorizerRegistration<")
                    .Append(Type(iface.TypeArguments[0])).Append(", ").Append(Type(type)).AppendLine(">(stage));");
                continue;
            }
            else if (role == "behavior")
            {
                var behaviorDescriptor = iface.OriginalDefinition.MetadataName == "IStreamRequestPipelineBehavior`2"
                    ? "StreamRequestPipelineBehaviorRegistration"
                    : "RequestPipelineBehaviorRegistration";
                _ = body.Append("_ = builder.AddGeneratedBehavior(new global::Cntryl.Portia.")
                    .Append(behaviorDescriptor).Append('<')
                    .Append(Type(iface.TypeArguments[0])).Append(", ").Append(Type(type));
                if (iface.TypeArguments.Length == 2)
                    _ = body.Append(", ").Append(Type(iface.TypeArguments[1]));

                _ = body.AppendLine(">(order));");
                continue;
            }

            var request = iface.TypeArguments[0];
            var diagnostic = PermissionDiagnostic(request, invocation.GetLocation());
            var descriptor = iface.OriginalDefinition.MetadataName == "IStreamRequestHandler`2"
                ? "StreamRequestRegistration"
                : "RequestRegistration";
            _ = body.Append("_ = builder.AddGeneratedHandler(new global::Cntryl.Portia.").Append(descriptor).Append('<')
                .Append(Type(request)).Append(", ").Append(Type(type));
            if (iface.TypeArguments.Length == 2)
                _ = body.Append(", ").Append(Type(iface.TypeArguments[1]));

            _ = body.Append(">(").Append(Permission(request, diagnostic)).AppendLine("));");
            if (RequestTransportDiscovery.GetRequestTransportComponent(request) is { } transport)
            {
                _ = body.Append("_ = builder.AddGeneratedRequest(").Append(RequestExpression(transport))
                    .AppendLine(");");
            }
        }

        return new Call(InterceptableLocationModel.From(location), role, body.ToString(), null,
            DiagnosticLocation.From(invocation.GetLocation()), permissionDiagnostic);
    }

    static string? RegistrationRole(IMethodSymbol method)
    {
        var containingType = method.ContainingType.ToDisplayString();
        return method.Name == "AddPortia" &&
               containingType == "Cntryl.Portia.PortiaApplicationServiceCollectionExtensions"
            ? "application"
            : containingType == "Cntryl.Portia.PortiaBuilder" && method.TypeArguments.Length == 1
                ? method.Name switch
                {
                    "AddRequestHandler" => "handler",
                    "AddRequestAuthorizer" => "authorizer",
                    "AddRequestPipelineBehavior" => "behavior",
                    "RegisterDynamicRequest" => "dynamic request",
                    "AddEvent" => "domain event",
                    _ => null
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
            or "Cntryl.Portia.RequestSenderContextExtensions"
            or "Cntryl.Portia.PortiaBuilder"))
        {
            return null;
        }

        var requestParameter = method.Parameters.FirstOrDefault(parameter => parameter.Name == "request");
        if (requestParameter == null)
            return null;

        var argument = invocation.ArgumentList.Arguments.FirstOrDefault(argument =>
                           argument.NameColon?.Name.Identifier.ValueText == "request")
                       ?? invocation.ArgumentList.Arguments.ElementAtOrDefault(requestParameter.Ordinal);
        if (argument == null)
        {
            return null;
        }
        else
        {
            var type = context.SemanticModel.GetTypeInfo(argument.Expression).Type;
            return type is INamedTypeSymbol named
                ? RequestTransportDiscovery.GetRequestTransportComponent(named)
                : null;
        }
    }

    static JsonContextModelRecord? JsonContextModel(GeneratorSyntaxContext context)
    {
        var declaration = (TypeDeclarationSyntax)context.Node;
        return context.SemanticModel.GetDeclaredSymbol(declaration) is INamedTypeSymbol symbol
               && symbol.GetAttributes().Any(attribute =>
                   attribute.AttributeClass?.ToDisplayString() == "Cntryl.Portia.PortiaJsonContextAttribute")
            ? new JsonContextModelRecord(symbol.ToDisplayString(), Type(symbol))
            : null;
    }

    static EventModelRecord? DeclaredEventModel(GeneratorSyntaxContext context)
    {
        var declaration = (TypeDeclarationSyntax)context.Node;
        return context.SemanticModel.GetDeclaredSymbol(declaration) is INamedTypeSymbol symbol
               && IsRegistrableEvent(symbol, true)
            ? EventModel(symbol)
            : null;
    }

    // Type syntax covers the semantic edges that make an external event part of this application:
    // handler interfaces, aggregate On<TEvent> calls, method signatures, construction, casts, and
    // explicit generic dispatch. A project reference by itself is deliberately not such an edge.
    static EventModelRecord? ReferencedEventModel(GeneratorSyntaxContext context)
    {
        return context.SemanticModel.GetTypeInfo((TypeSyntax)context.Node).Type is INamedTypeSymbol type
               && !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly,
                   context.SemanticModel.Compilation.Assembly)
               && IsRegistrableEvent(type, false)
            ? EventModel(type)
            : null;
    }

    // One discriminator cannot describe every constructed form of a generic event. This also
    // keeps open type parameters out of the generated, non-generic AddPortia interceptor.
    static bool IsRegistrableEvent(INamedTypeSymbol symbol, bool requireSameAssembly) =>
        !symbol.IsGenericType && IsDomainEvent(symbol, requireSameAssembly);

    static EventModelRecord? EventModel(INamedTypeSymbol symbol)
    {
        var attribute = symbol.GetAttributes().FirstOrDefault(candidate =>
            candidate.AttributeClass?.ToDisplayString() == "Cntryl.Portia.DiscriminatorAttribute");
        return attribute is null || attribute.ConstructorArguments.Length != 2
            ? null
            : new EventModelRecord(symbol.ToDisplayString(), Type(symbol),
                attribute.ConstructorArguments[0].Value as string ?? string.Empty,
                attribute.ConstructorArguments[1].Value as int? ?? 0);
    }

    static SharedRegistrationsModel SharedRegistrations(
        ImmutableArray<JsonContextModelRecord> jsonContexts,
        ImmutableArray<EventModelRecord> declaredEvents,
        ImmutableArray<EventModelRecord> referencedEvents)
    {
        var contexts = new StringBuilder();
        foreach (var context in Unique(jsonContexts, model => model.DisplayName))
        {
            _ = contexts.Append("_ = builder.AddGeneratedJsonContext(static options => new ")
                .Append(context.TypeName).AppendLine("(options));");
        }

        var events = new StringBuilder();
        foreach (var model in Unique(declaredEvents.Concat(referencedEvents), model => model.DisplayName))
        {
            _ = events.Append("_ = builder.AddGeneratedEvent<").Append(model.TypeName).Append(">(")
                .Append(model.Version).Append(", ")
                .Append(RequestTransportDiscovery.FormatStringLiteral(model.Name)).AppendLine(");");
        }

        return new SharedRegistrationsModel(contexts.ToString(), events.ToString());
    }

    // One entry per declared type, ordered by its display name — a partial declaration reaches the
    // pipeline once per part, and a referenced event once per syntax that names it.
    static IEnumerable<T> Unique<T>(IEnumerable<T> models, Func<T, string> key) =>
        models.GroupBy(key, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.First());

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

    static string Permission(ITypeSymbol request, RegistrationDiagnostic? diagnostic)
    {
        if (request.GetAttributes()
                .FirstOrDefault(attribute =>
                    attribute.AttributeClass?.ToDisplayString() == "Cntryl.Portia.RequiresPermissionAttribute")
                ?.ConstructorArguments.FirstOrDefault().Value is not string value)
        {
            return "null";
        }

        if (diagnostic is not null)
            return "null"; // The generated interceptor throws before any registration mutation.

        var properties = RequestProperties(request).ToArray();
        // A permission string is a stable identifier an application looks up in its own policy
        // store, so an interpolated property must render the same on every host. Plain $"..."
        // formats with the ambient culture, which changes a negative id's sign character and a
        // decimal or date entirely.
        var expression = new StringBuilder(
            "static typed => string.Create(global::System.Globalization.CultureInfo.InvariantCulture, $\"");
        var offset = 0;
        foreach (Match match in PermissionTokenPattern.Matches(value))
        {
            _ = expression.Append(Escape(value.Substring(offset, match.Index - offset)));
            var property = properties.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, match.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            if (property == null)
            {
                return "null"; // PermissionDiagnostic reports PORTIA011 before source emission.
            }
            else
            {
                _ = expression.Append("{typed.").Append(property.Name).Append('}');
                offset = match.Index + match.Length;
            }
        }

        return expression.Append(Escape(value.Substring(offset))).Append("\")").ToString();
    }

    static RegistrationDiagnostic? PermissionDiagnostic(ITypeSymbol request, Location location)
    {
        if (request.GetAttributes()
                .FirstOrDefault(attribute =>
                    attribute.AttributeClass?.ToDisplayString() == "Cntryl.Portia.RequiresPermissionAttribute")
                ?.ConstructorArguments.FirstOrDefault().Value is not string value)
        {
            return null;
        }

        var properties = RequestProperties(request).ToArray();
        foreach (Match match in PermissionTokenPattern.Matches(value))
        {
            var token = match.Groups[1].Value;
            var property = properties.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, token, StringComparison.OrdinalIgnoreCase));
            if (property == null)
            {
                return new RegistrationDiagnostic(RegistrationDiagnosticKind.UnknownPermissionToken,
                    DiagnosticLocation.From(location), Type(request), token);
            }

            if (property.NullableAnnotation == NullableAnnotation.Annotated
                || property.Type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            {
                return new RegistrationDiagnostic(RegistrationDiagnosticKind.NullablePermissionToken,
                    DiagnosticLocation.From(location), Type(request), token);
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

    static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("{", "{{").Replace("}", "}}");

    static string EscapeLiteral(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    static string Type(ITypeSymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    static void Generate(SourceProductionContext context, ImmutableArray<Call> calls,
        ImmutableArray<RequestTransportComponent> dispatchedRequests, SharedRegistrationsModel shared)
    {
        foreach (var call in calls)
        {
            if (call.Diagnostic is not null)
                context.ReportDiagnostic(call.Diagnostic.ToDiagnostic());
            if (call.Error is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidRegistration, call.DiagnosticLocation.ToLocation(),
                    call.Role, call.Role, call.Error));
            }
        }

        var inferred = new StringBuilder();
        foreach (var request in dispatchedRequests.GroupBy(request => request.TypeName, StringComparer.Ordinal)
                     .Select(group => group.First()))
        {
            _ = inferred.Append("_ = builder.AddGeneratedRequest(").Append(RequestExpression(request)).AppendLine(");");
        }

        var valid = calls.Where(c => c.Body is not null && c.Location is not null).ToArray();
        if (valid.Length == 0)
            return;

        var source = new StringBuilder().AppendLine("// <auto-generated />").AppendLine("#nullable enable")
            .AppendLine(
                "namespace System.Runtime.CompilerServices { [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)] file sealed class InterceptsLocationAttribute : global::System.Attribute { public InterceptsLocationAttribute(int version, string data) { _ = version; _ = data; } } }")
            .AppendLine("namespace Cntryl.Portia.Generated { file static class PortiaRegistrationInterceptors {");
        for (var i = 0; i < valid.Length; i++)
        {
            var call = valid[i];
            _ = source.Append("[global::System.Runtime.CompilerServices.InterceptsLocation(")
                .Append(call.Location!.Value.Version).Append(", \"").Append(call.Location.Value.Data)
                .AppendLine("\")]");
            _ = call.Role == "application"
                ? source.Append("public static global::Cntryl.Portia.PortiaBuilder Register").Append(i)
                    .AppendLine("(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services) {")
                    .AppendLine("global::System.ArgumentNullException.ThrowIfNull(services);")
                    .AppendLine(
                        "var builder = global::Cntryl.Portia.PortiaApplicationServiceCollectionExtensions.AddPortia(services);")
                    .Append(shared.JsonContexts).Append(call.Body).Append(shared.Events).Append(inferred)
                    .AppendLine("return builder;").AppendLine("}")
                : source.Append("public static global::Cntryl.Portia.PortiaBuilder Register").Append(i)
                    .Append(call.Role switch
                    {
                        "authorizer" =>
                            "(this global::Cntryl.Portia.PortiaBuilder builder, global::Cntryl.Portia.AuthorizationStage stage) {\n",
                        "behavior" => "(this global::Cntryl.Portia.PortiaBuilder builder, int order) {\n",
                        _ => "(this global::Cntryl.Portia.PortiaBuilder builder) {\n"
                    })
                    .AppendLine("global::System.ArgumentNullException.ThrowIfNull(builder);").Append(call.Body)
                    .Append(shared.Events).AppendLine("return builder;").AppendLine("}");
        }

        _ = source.AppendLine("} }");
        context.AddSource("PortiaGeneratedRegistrationInterceptors.g.cs",
            SourceText.From(source.ToString(), Encoding.UTF8));
    }

    sealed record JsonContextModelRecord(string DisplayName, string TypeName);

    sealed record EventModelRecord(string DisplayName, string TypeName, string Name, int Version);

    sealed record SharedRegistrationsModel(string JsonContexts, string Events);

    sealed record Call(
        InterceptableLocationModel? Location,
        string Role,
        string? Body,
        string? Error,
        DiagnosticLocation DiagnosticLocation,
        RegistrationDiagnostic? Diagnostic = null);

    enum RegistrationDiagnosticKind
    {
        UnsupportedCallSite,
        UnknownPermissionToken,
        NullablePermissionToken
    }

    sealed record RegistrationDiagnostic(
        RegistrationDiagnosticKind Kind,
        DiagnosticLocation Location,
        string FirstArgument,
        string? SecondArgument = null)
    {
        public Diagnostic ToDiagnostic() => Kind switch
        {
            RegistrationDiagnosticKind.UnsupportedCallSite =>
                Diagnostic.Create(UnsupportedRegistrationCallSite, Location.ToLocation(), FirstArgument),
            RegistrationDiagnosticKind.UnknownPermissionToken =>
                Diagnostic.Create(UnknownPermissionToken, Location.ToLocation(), FirstArgument, SecondArgument),
            RegistrationDiagnosticKind.NullablePermissionToken =>
                Diagnostic.Create(NullablePermissionToken, Location.ToLocation(), FirstArgument, SecondArgument),
            _ => throw new ArgumentOutOfRangeException(nameof(Kind))
        };
    }
}
