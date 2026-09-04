using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia;

/// <summary>
/// Discovers every request-handler and authorizer interface and generates typed descriptors for
/// the shared scoped dispatcher. Permission expressions are validated and emitted at compile time.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class RequestBusGenerator : IIncrementalGenerator
{
    const string RequestHandlerMetadataName = "IRequestHandler`1";
    const string RequestHandlerWithResultMetadataName = "IRequestHandler`2";
    const string StreamRequestHandlerMetadataName = "IStreamRequestHandler`2";
    const string RequestAuthorizerMetadataName = "IRequestAuthorizer`1";
    const string RequiresPermissionAttributeMetadataName = "Cntryl.Portia.RequiresPermissionAttribute";

    static readonly DiagnosticDescriptor DuplicateRequestHandler = new(
        "PORTIA008",
        "Request has more than one handler",
        "Request type '{0}' has more than one registered handler: '{1}' and '{2}'",
        "Portia",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor DuplicateRequestAuthorizer = new(
        "PORTIA010",
        "Request has more than one authorizer",
        "Request type '{0}' has more than one registered IRequestAuthorizer<>: '{1}' and '{2}'",
        "Portia",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor UnknownPermissionToken = new(
        "PORTIA011",
        "Unknown permission token",
        "RequiresPermission on '{0}' references '{{{1}}}', which does not match any of its primary constructor's parameters",
        "Portia",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor NullablePermissionToken = new(
        "PORTIA013",
        "Permission token references a nullable property",
        "RequiresPermission on '{0}' references '{{{1}}}', which is nullable — a null value at " +
        "dispatch time would silently collapse to an empty segment in the checked permission " +
        "string instead of failing clearly; use a non-nullable property",
        "Portia",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly Regex PermissionTokenPattern = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var handlers = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (syntaxContext, _) => GetRequestHandler(syntaxContext))
            .SelectMany(static (handlers, _) => handlers)
            .Collect();

        var authorizers = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (syntaxContext, _) => GetRequestAuthorizer(syntaxContext))
            .SelectMany(static (authorizers, _) => authorizers)
            .Collect();

        context.RegisterSourceOutput(
            handlers.Combine(authorizers),
            static (sourceContext, pair) => Generate(sourceContext, pair.Left, pair.Right));
    }

    static IEnumerable<RequestHandlerModel> GetRequestHandler(GeneratorSyntaxContext context)
    {
        var declaration = (ClassDeclarationSyntax)context.Node;

        if (context.SemanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol || symbol.IsAbstract || !GeneratedTypeShape.IsSupported(symbol))
            yield break;

        foreach (var iface in symbol.AllInterfaces)
        {
            if (iface.OriginalDefinition.ContainingNamespace.ToDisplayString() != "Cntryl.Portia")
                continue;

            if (iface.OriginalDefinition.MetadataName == RequestHandlerMetadataName)
            {
                yield return new RequestHandlerModel(
                    symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    resultType: null,
                    HandlerKind.NoResult,
                    GetRequiredPermission(iface.TypeArguments[0]),
                    GetRequestParameterNames(iface.TypeArguments[0]),
                    declaration.Identifier.GetLocation());
            }

            if (iface.OriginalDefinition.MetadataName == RequestHandlerWithResultMetadataName)
            {
                yield return new RequestHandlerModel(
                    symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    iface.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    HandlerKind.WithResult,
                    GetRequiredPermission(iface.TypeArguments[0]),
                    GetRequestParameterNames(iface.TypeArguments[0]),
                    declaration.Identifier.GetLocation());
            }

            if (iface.OriginalDefinition.MetadataName == StreamRequestHandlerMetadataName)
            {
                yield return new RequestHandlerModel(
                    symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    iface.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    HandlerKind.Stream,
                    GetRequiredPermission(iface.TypeArguments[0]),
                    GetRequestParameterNames(iface.TypeArguments[0]),
                    declaration.Identifier.GetLocation());
            }
        }

        yield break;
    }

    static string? GetRequiredPermission(ITypeSymbol requestType) =>
        requestType.GetAttributes()
            .Where(attribute => attribute.AttributeClass?.ToDisplayString() == RequiresPermissionAttributeMetadataName)
            .Select(attribute => attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is string permission
                ? permission
                : null)
            .FirstOrDefault(permission => permission is not null);

    // Backs {Token} interpolation in a RequiresPermission string — the same "match a request's
    // primary-constructor parameter by name" convention RequestHttpBindingGenerator already uses
    // for route tokens, applied here to permission strings instead.
    static RequestParameterModel[] GetRequestParameterNames(ITypeSymbol requestType) =>
        requestType is INamedTypeSymbol { InstanceConstructors: var constructors }
            ? constructors
                .Where(ctor => ctor.Parameters.Length > 0 && ctor.DeclaredAccessibility == Accessibility.Public)
                .OrderByDescending(ctor => ctor.Parameters.Length)
                .FirstOrDefault()
                ?.Parameters.Select(p => new RequestParameterModel(p.Name, IsNullableParameterType(p.Type))).ToArray() ?? []
            : [];

    static bool IsNullableParameterType(ITypeSymbol type) =>
        type.NullableAnnotation == NullableAnnotation.Annotated
        || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    static IEnumerable<AuthorizerModel> GetRequestAuthorizer(GeneratorSyntaxContext context)
    {
        var declaration = (ClassDeclarationSyntax)context.Node;

        if (context.SemanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol { IsAbstract: false } symbol || !GeneratedTypeShape.IsSupported(symbol))
            yield break;

        foreach (var iface in symbol.AllInterfaces)
        {
            if (iface.OriginalDefinition.ContainingNamespace.ToDisplayString() != "Cntryl.Portia"
                || iface.OriginalDefinition.MetadataName != RequestAuthorizerMetadataName)
            {
                continue;
            }

            yield return new AuthorizerModel(
                symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                declaration.Identifier.GetLocation());
        }

        yield break;
    }

    static void Generate(SourceProductionContext context, ImmutableArray<RequestHandlerModel> handlers, ImmutableArray<AuthorizerModel> authorizers)
    {
        if (handlers.IsDefaultOrEmpty && authorizers.IsDefaultOrEmpty)
            return;

        var ordered = handlers
            .GroupBy(handler => (handler.RequestType, handler.HandlerType))
            .Select(group => group.First())
            .OrderBy(handler => handler.RequestType, StringComparer.Ordinal)
            .ToArray();

        foreach (var group in ordered.GroupBy(handler => handler.RequestType, StringComparer.Ordinal))
        {
            if (group.Count() <= 1)
                continue;

            var first = group.First();
            var second = group.Skip(1).First();

            context.ReportDiagnostic(Diagnostic.Create(
                DuplicateRequestHandler,
                second.Location,
                first.RequestType,
                first.HandlerType,
                second.HandlerType));
            return;
        }

        foreach (var handler in ordered.Where(handler => handler.Permission is not null))
        {
            foreach (Match match in PermissionTokenPattern.Matches(handler.Permission))
            {
                var token = match.Groups[1].Value;
                var parameter = handler.RequestParameters.FirstOrDefault(p => string.Equals(p.Name, token, StringComparison.OrdinalIgnoreCase));

                if (parameter is null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(UnknownPermissionToken, handler.Location, handler.RequestType, token));
                    return;
                }

                if (parameter.IsNullable)
                {
                    context.ReportDiagnostic(Diagnostic.Create(NullablePermissionToken, handler.Location, handler.RequestType, token));
                    return;
                }
            }
        }

        authorizers = [.. authorizers.GroupBy(authorizer => (authorizer.RequestType, authorizer.AuthorizerType)).Select(group => group.First())];
        foreach (var group in authorizers.GroupBy(authorizer => authorizer.RequestType, StringComparer.Ordinal))
        {
            if (group.Count() <= 1)
                continue;

            var first = group.First();
            var second = group.Skip(1).First();

            context.ReportDiagnostic(Diagnostic.Create(
                DuplicateRequestAuthorizer,
                second.Location,
                first.RequestType,
                first.AuthorizerType,
                second.AuthorizerType));
            return;
        }

        var source = new StringBuilder()
            .AppendLine("// <auto-generated />")
            .AppendLine("#nullable enable")
            .AppendLine("namespace Cntryl.Portia;")
            .AppendLine("internal static class PortiaGeneratedRequestRegistrations")
            .AppendLine("{")
            .AppendLine("    public static void Register(global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)")
            .AppendLine("    {")
            .AppendLine("        global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddScoped<global::Cntryl.Portia.IRequestBus, global::Cntryl.Portia.RequestBus>(services);")
            .AppendLine("        global::Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddSingleton<global::Cntryl.Portia.RequestRegistry>(services);");

        foreach (var handler in ordered)
        {
            var descriptor = handler.Kind == HandlerKind.Stream ? "StreamRequestRegistration" : "RequestRegistration";
            var typeArguments = handler.RequestType + ", " + handler.HandlerType
                + (handler.ResultType is null ? "" : ", " + handler.ResultType);
            var permission = handler.Permission is null ? "null" : "static typed => " + BuildPermissionExpression(handler);
            _ = source.Append("        _ = global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<global::Cntryl.Portia.RequestHandlerRegistration>(services, new global::Cntryl.Portia.")
                .Append(descriptor).Append('<').Append(typeArguments).Append(">(").Append(permission).AppendLine("));");
        }
        foreach (var authorizer in authorizers)
        {
            _ = source.Append("        _ = global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<global::Cntryl.Portia.RequestAuthorizerRegistration>(services, new global::Cntryl.Portia.RequestAuthorizerRegistration<")
                .Append(authorizer.RequestType).Append(", ").Append(authorizer.AuthorizerType).AppendLine(">());");
        }
        _ = source.AppendLine("    }").AppendLine("}");
        context.AddSource("PortiaGeneratedRequestRegistrations.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    // Builds the C# source expression evaluated at dispatch time for a RequiresPermission
    // string: a plain literal when it has no {Token}s, or an interpolated string pulling each
    // token's value straight off the concrete request (`typed.PropertyName`) otherwise — token
    // validity against the request's own shape was already checked in Generate.
    static string BuildPermissionExpression(RequestHandlerModel handler)
    {
        var permission = handler.Permission!;
        var matches = PermissionTokenPattern.Matches(permission);

        if (matches.Count == 0)
            return "\"" + EscapeStringLiteral(permission) + "\"";

        var expression = new StringBuilder("$\"");
        var lastIndex = 0;

        foreach (Match match in matches)
        {
            _ = expression.Append(EscapeInterpolatedSegment(permission.Substring(lastIndex, match.Index - lastIndex)));

            var token = match.Groups[1].Value;
            var parameterName = handler.RequestParameters.First(p => string.Equals(p.Name, token, StringComparison.OrdinalIgnoreCase)).Name;
            _ = expression.Append("{typed.").Append(parameterName).Append('}');

            lastIndex = match.Index + match.Length;
        }

        _ = expression.Append(EscapeInterpolatedSegment(permission.Substring(lastIndex))).Append('"');
        return expression.ToString();
    }

    static string EscapeStringLiteral(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // A static segment feeding into an interpolated string literal ($"...") also needs any
    // literal '{'/'}' doubled, so it isn't misread as another interpolation hole.
    static string EscapeInterpolatedSegment(string value) => EscapeStringLiteral(value).Replace("{", "{{").Replace("}", "}}");

    enum HandlerKind
    {
        NoResult,
        WithResult,
        Stream,
    }

    sealed class RequestHandlerModel(
        string handlerType,
        string requestType,
        string? resultType,
        HandlerKind kind,
        string? permission,
        RequestParameterModel[] requestParameters,
        Location location)
    {
        public string HandlerType { get; } = handlerType;

        public string RequestType { get; } = requestType;

        public string? ResultType { get; } = resultType;

        public HandlerKind Kind { get; } = kind;

        public string? Permission { get; } = permission;

        public RequestParameterModel[] RequestParameters { get; } = requestParameters;

        public Location Location { get; } = location;
    }

    // Tracked alongside each request's primary-constructor parameter names specifically so a
    // {Token} interpolated into a RequiresPermission string can be checked for nullability
    // (PORTIA013), not just that the name exists at all (PORTIA011) — a null value at dispatch
    // time otherwise silently collapses to an empty segment in the checked permission string.
    sealed class RequestParameterModel(string name, bool isNullable)
    {
        public string Name { get; } = name;

        public bool IsNullable { get; } = isNullable;
    }

    sealed class AuthorizerModel(string authorizerType, string requestType, Location location)
    {
        public string AuthorizerType { get; } = authorizerType;

        public string RequestType { get; } = requestType;

        public Location Location { get; } = location;
    }
}
