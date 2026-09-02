using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia;

/// <summary>
/// Generates a request bus that dispatches every request discovered in the compilation to its
/// single handler, using compile-time discovery instead of runtime reflection. Also wires each
/// request's declared authorization — <c>[RequiresPermission]</c> and/or
/// <c>IRequestAuthorizer&lt;TRequest&gt;</c> — in ahead of its handler, so every transport gets
/// the same checks for free by funneling through this one generated dispatch point.
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

    static readonly Regex PermissionTokenPattern = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var handlers = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (syntaxContext, _) => GetRequestHandler(syntaxContext))
            .Where(static handler => handler is not null)
            .Select(static (handler, _) => handler!)
            .Collect();

        var authorizers = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax,
                static (syntaxContext, _) => GetRequestAuthorizer(syntaxContext))
            .Where(static authorizer => authorizer is not null)
            .Select(static (authorizer, _) => authorizer!)
            .Collect();

        context.RegisterSourceOutput(
            handlers.Combine(authorizers),
            static (sourceContext, pair) => Generate(sourceContext, pair.Left, pair.Right));
    }

    static RequestHandlerModel? GetRequestHandler(GeneratorSyntaxContext context)
    {
        var declaration = (ClassDeclarationSyntax)context.Node;

        if (context.SemanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol symbol || symbol.IsAbstract)
            return null;

        foreach (var iface in symbol.AllInterfaces)
        {
            if (iface.OriginalDefinition.ContainingNamespace.ToDisplayString() != "Cntryl.Portia")
                continue;

            if (iface.OriginalDefinition.MetadataName == RequestHandlerMetadataName)
            {
                return new RequestHandlerModel(
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
                return new RequestHandlerModel(
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
                return new RequestHandlerModel(
                    symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    iface.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    HandlerKind.Stream,
                    GetRequiredPermission(iface.TypeArguments[0]),
                    GetRequestParameterNames(iface.TypeArguments[0]),
                    declaration.Identifier.GetLocation());
            }
        }

        return null;
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
    static string[] GetRequestParameterNames(ITypeSymbol requestType) =>
        requestType is INamedTypeSymbol { InstanceConstructors: var constructors }
            ? constructors
                .Where(ctor => ctor.Parameters.Length > 0 && ctor.DeclaredAccessibility == Accessibility.Public)
                .OrderByDescending(ctor => ctor.Parameters.Length)
                .FirstOrDefault()
                ?.Parameters.Select(p => p.Name).ToArray() ?? []
            : [];

    static AuthorizerModel? GetRequestAuthorizer(GeneratorSyntaxContext context)
    {
        var declaration = (ClassDeclarationSyntax)context.Node;

        if (context.SemanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol { IsAbstract: false } symbol)
            return null;

        foreach (var iface in symbol.AllInterfaces)
        {
            if (iface.OriginalDefinition.ContainingNamespace.ToDisplayString() != "Cntryl.Portia"
                || iface.OriginalDefinition.MetadataName != RequestAuthorizerMetadataName)
            {
                continue;
            }

            return new AuthorizerModel(
                symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                iface.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                declaration.Identifier.GetLocation());
        }

        return null;
    }

    static void Generate(SourceProductionContext context, ImmutableArray<RequestHandlerModel> handlers, ImmutableArray<AuthorizerModel> authorizers)
    {
        if (handlers.IsDefaultOrEmpty)
            return;

        var ordered = handlers
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

                if (!handler.RequestParameterNames.Any(name => string.Equals(name, token, StringComparison.OrdinalIgnoreCase)))
                {
                    context.ReportDiagnostic(Diagnostic.Create(UnknownPermissionToken, handler.Location, handler.RequestType, token));
                    return;
                }
            }
        }

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

        var authorizerByRequestType = authorizers
            .GroupBy(authorizer => authorizer.RequestType, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().AuthorizerType, StringComparer.Ordinal);

        var hasAnyPermission = ordered.Any(handler => handler.Permission is not null);

        var source = new StringBuilder()
            .AppendLine("// <auto-generated />")
            .AppendLine("#nullable enable")
            .AppendLine("namespace Cntryl.Portia;")
            .AppendLine()
            .AppendLine("/// <summary>")
            .AppendLine("/// Dispatches every request discovered in this compilation to its single handler, after")
            .AppendLine("/// running its declared authorization (if any) against the given actor.")
            .AppendLine("/// </summary>")
            .AppendLine("internal sealed class GeneratedRequestBus : global::Cntryl.Portia.IRequestBus")
            .AppendLine("{");

        foreach (var handler in ordered)
        {
            _ = source
                .Append("    readonly ")
                .Append(handler.HandlerType)
                .Append(' ')
                .Append(FieldName(handler.HandlerType))
                .AppendLine(";");
        }

        foreach (var authorizerType in authorizerByRequestType.Values.Distinct(StringComparer.Ordinal))
        {
            _ = source
                .Append("    readonly ")
                .Append(authorizerType)
                .Append(' ')
                .Append(FieldName(authorizerType))
                .AppendLine(";");
        }

        if (hasAnyPermission)
        {
            _ = source.AppendLine("    readonly global::Cntryl.Portia.IPermissionEvaluator _permissionEvaluator;");
        }

        var constructorParameters = ordered.Select(handler => (handler.HandlerType, ParameterName(handler.HandlerType)))
            .Concat(authorizerByRequestType.Values.Distinct(StringComparer.Ordinal).Select(type => (type, ParameterName(type))))
            .Concat(hasAnyPermission
                ? [("global::Cntryl.Portia.IPermissionEvaluator", "permissionEvaluator")]
                : [])
            .ToArray();

        _ = source
            .AppendLine()
            .AppendLine("    /// <summary>")
            .AppendLine("    /// Creates a request bus over every handler discovered in this compilation.")
            .AppendLine("    /// </summary>")
            .Append("    public GeneratedRequestBus(")
            .Append(string.Join(", ", constructorParameters.Select(p => $"{p.Item1} {p.Item2}")))
            .AppendLine(")")
            .AppendLine("    {");

        foreach (var handler in ordered)
        {
            _ = source
                .Append("        ")
                .Append(FieldName(handler.HandlerType))
                .Append(" = ")
                .Append(ParameterName(handler.HandlerType))
                .AppendLine(";");
        }

        foreach (var authorizerType in authorizerByRequestType.Values.Distinct(StringComparer.Ordinal))
        {
            _ = source
                .Append("        ")
                .Append(FieldName(authorizerType))
                .Append(" = ")
                .Append(ParameterName(authorizerType))
                .AppendLine(";");
        }

        if (hasAnyPermission)
        {
            _ = source.AppendLine("        _permissionEvaluator = permissionEvaluator;");
        }

        _ = source
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    /// <inheritdoc />")
            .AppendLine("    public async global::System.Threading.Tasks.ValueTask<global::Cntryl.Portia.Result> SendAsync(")
            .AppendLine("        global::Cntryl.Portia.IRequest request,")
            .AppendLine("        global::System.Security.Claims.ClaimsPrincipal actor,")
            .AppendLine("        global::System.Threading.CancellationToken ct = default)")
            .AppendLine("    {")
            .AppendLine("        switch (request)")
            .AppendLine("        {");

        foreach (var handler in ordered.Where(handler => handler.Kind == HandlerKind.NoResult))
        {
            var authorizerType = authorizerByRequestType.TryGetValue(handler.RequestType, out var found) ? found : null;

            _ = source
                .Append("            case ")
                .Append(handler.RequestType)
                .AppendLine(" typed:")
                .AppendLine("            {");
            AppendActivityStart(source, handler, "                ");
            AppendAuthorizationChecks(source, handler, authorizerType, "                ", isStream: false);
            _ = source
                .Append("                var result = await ")
                .Append(FieldName(handler.HandlerType))
                .Append(".HandleAsync(new global::Cntryl.Portia.RequestContext<")
                .Append(handler.RequestType)
                .AppendLine(">(typed), ct).ConfigureAwait(false);")
                .AppendLine("                global::Cntryl.Portia.PortiaTelemetry.RecordOutcome(activity, result.IsSuccess, result.Error);")
                .AppendLine("                return result;")
                .AppendLine("            }");
        }

        _ = source
            .AppendLine("            default:")
            .AppendLine("                throw new global::System.InvalidOperationException(")
            .AppendLine("                    $\"No handler is registered for request type '{request.GetType()}'.\");")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    /// <inheritdoc />")
            .AppendLine("    public async global::System.Threading.Tasks.ValueTask<global::Cntryl.Portia.Result<TOut>> SendAsync<TOut>(")
            .AppendLine("        global::Cntryl.Portia.IRequest<TOut> request,")
            .AppendLine("        global::System.Security.Claims.ClaimsPrincipal actor,")
            .AppendLine("        global::System.Threading.CancellationToken ct = default)")
            .AppendLine("    {")
            .AppendLine("        switch (request)")
            .AppendLine("        {");

        foreach (var handler in ordered.Where(handler => handler.Kind == HandlerKind.WithResult))
        {
            var authorizerType = authorizerByRequestType.TryGetValue(handler.RequestType, out var found) ? found : null;

            _ = source
                .Append("            case ")
                .Append(handler.RequestType)
                .AppendLine(" typed:")
                .AppendLine("            {");
            AppendActivityStart(source, handler, "                ");
            AppendAuthorizationChecks(source, handler, authorizerType, "                ", isStream: false, resultTypeParameter: "TOut");
            _ = source
                .Append("                var result = (global::Cntryl.Portia.Result<TOut>)(object)await ")
                .Append(FieldName(handler.HandlerType))
                .Append(".HandleAsync(new global::Cntryl.Portia.RequestContext<")
                .Append(handler.RequestType)
                .AppendLine(">(typed), ct).ConfigureAwait(false);")
                .AppendLine("                global::Cntryl.Portia.PortiaTelemetry.RecordOutcome(activity, result.IsSuccess, result.Error);")
                .AppendLine("                return result;")
                .AppendLine("            }");
        }

        _ = source
            .AppendLine("            default:")
            .AppendLine("                throw new global::System.InvalidOperationException(")
            .AppendLine("                    $\"No handler is registered for request type '{request.GetType()}'.\");")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine()
            .AppendLine("    /// <inheritdoc />")
            .AppendLine("    public global::System.Collections.Generic.IAsyncEnumerable<TOut> StreamAsync<TOut>(")
            .AppendLine("        global::Cntryl.Portia.IStreamRequest<TOut> request,")
            .AppendLine("        global::System.Security.Claims.ClaimsPrincipal actor,")
            .AppendLine("        global::System.Threading.CancellationToken ct = default)")
            .AppendLine("    {")
            .AppendLine("        switch (request)")
            .AppendLine("        {");

        var streamIndex = 0;

        foreach (var handler in ordered.Where(handler => handler.Kind == HandlerKind.Stream))
        {
            _ = source
                .Append("            case ")
                .Append(handler.RequestType)
                .AppendLine(" typed:")
                .Append("                return (global::System.Collections.Generic.IAsyncEnumerable<TOut>)(object)Stream_")
                .Append(streamIndex)
                .AppendLine("(typed, actor, ct);");
            streamIndex++;
        }

        _ = source
            .AppendLine("            default:")
            .AppendLine("                throw new global::System.InvalidOperationException(")
            .AppendLine("                    $\"No handler is registered for request type '{request.GetType()}'.\");")
            .AppendLine("        }")
            .AppendLine("    }");

        streamIndex = 0;

        foreach (var handler in ordered.Where(handler => handler.Kind == HandlerKind.Stream))
        {
            var authorizerType = authorizerByRequestType.TryGetValue(handler.RequestType, out var found) ? found : null;

            _ = source
                .AppendLine()
                .Append("    async global::System.Collections.Generic.IAsyncEnumerable<")
                .Append(handler.ResultType)
                .Append("> Stream_")
                .Append(streamIndex)
                .Append('(')
                .Append(handler.RequestType)
                .AppendLine(" typed,")
                .AppendLine("        global::System.Security.Claims.ClaimsPrincipal actor,")
                .AppendLine("        [global::System.Runtime.CompilerServices.EnumeratorCancellation] global::System.Threading.CancellationToken ct)")
                .AppendLine("    {");
            AppendActivityStart(source, handler, "        ");
            AppendAuthorizationChecks(source, handler, authorizerType, "        ", isStream: true);
            _ = source
                .Append("        await foreach (var item in ")
                .Append(FieldName(handler.HandlerType))
                .Append(".HandleAsync(new global::Cntryl.Portia.RequestContext<")
                .Append(handler.RequestType)
                .AppendLine(">(typed), ct).WithCancellation(ct).ConfigureAwait(false))")
                .AppendLine("            yield return item;")
                .AppendLine("    }");

            streamIndex++;
        }

        _ = source.AppendLine("}");

        context.AddSource("GeneratedRequestBus.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    // Every transport funnels through this one dispatch point, so starting the activity here —
    // rather than per-transport — is what makes tracing "just work" the same way authorization
    // does: implemented once, applies everywhere. `using` disposes the activity (ending the span)
    // on every exit from its enclosing block, including an early return from a denied
    // authorization check, so no explicit "stop" call is needed anywhere else.
    static void AppendActivityStart(StringBuilder source, RequestHandlerModel handler, string indent)
    {
        _ = source
            .Append(indent).Append("using var activity = global::Cntryl.Portia.PortiaTelemetry.ActivitySource.StartActivity(\"Portia ")
            .Append(SimpleTypeName(handler.RequestType)).AppendLine("\");")
            .Append(indent).Append("_ = activity?.SetTag(\"portia.request_type\", \"").Append(SimpleTypeName(handler.RequestType)).AppendLine("\");");
    }

    static string SimpleTypeName(string fullyQualifiedType)
    {
        const string globalPrefix = "global::";
        var unqualified = fullyQualifiedType.StartsWith(globalPrefix, StringComparison.Ordinal)
            ? fullyQualifiedType.Substring(globalPrefix.Length)
            : fullyQualifiedType;
        return unqualified.Split('.').Last();
    }

    static void AppendAuthorizationChecks(
        StringBuilder source,
        RequestHandlerModel handler,
        string? authorizerType,
        string indent,
        bool isStream,
        string? resultTypeParameter = null)
    {
        if (handler.Permission is not null)
        {
            _ = source
                .Append(indent).AppendLine("var permissionResult = await _permissionEvaluator.EvaluateAsync(")
                .Append(indent).Append("    actor, ").Append(BuildPermissionExpression(handler)).AppendLine(", ct).ConfigureAwait(false);")
                .Append(indent).AppendLine("if (!permissionResult.IsSuccess)")
                .Append(indent).AppendLine("{");
            AppendAuthorizationFailure(source, indent + "    ", isStream, resultTypeParameter, "permissionResult.Error!");
            _ = source.Append(indent).AppendLine("}");
        }

        if (authorizerType is not null)
        {
            _ = source
                .Append(indent).Append("var authorizeResult = await ").Append(FieldName(authorizerType))
                .Append(".AuthorizeAsync(new global::Cntryl.Portia.RequestContext<").Append(handler.RequestType)
                .AppendLine(">(typed), actor, ct).ConfigureAwait(false);")
                .Append(indent).AppendLine("if (!authorizeResult.IsSuccess)")
                .Append(indent).AppendLine("{");
            AppendAuthorizationFailure(source, indent + "    ", isStream, resultTypeParameter, "authorizeResult.Error!");
            _ = source.Append(indent).AppendLine("}");
        }
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
            var parameterName = handler.RequestParameterNames.First(name => string.Equals(name, token, StringComparison.OrdinalIgnoreCase));
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

    static void AppendAuthorizationFailure(StringBuilder source, string indent, bool isStream, string? resultTypeParameter, string errorExpression)
    {
        if (isStream)
        {
            _ = source
                .Append(indent).Append("global::Cntryl.Portia.PortiaTelemetry.RecordOutcome(activity, false, ").Append(errorExpression).AppendLine(");")
                .Append(indent).Append("throw new global::Cntryl.Portia.RequestAuthorizationException(").Append(errorExpression).AppendLine(");");
            return;
        }

        _ = source
            .Append(indent).Append("global::Cntryl.Portia.PortiaTelemetry.RecordOutcome(activity, false, ").Append(errorExpression).AppendLine(");")
            .Append(indent)
            .Append("return ")
            .Append(resultTypeParameter is null ? "global::Cntryl.Portia.Result.Failure(" : $"global::Cntryl.Portia.Result<{resultTypeParameter}>.Failure(")
            .Append(errorExpression)
            .AppendLine(");");
    }

    static string FieldName(string handlerType) => "_" + ParameterName(handlerType);

    static string ParameterName(string handlerType)
    {
        // A type declared in the global namespace (e.g. top-level statements) has a fully
        // qualified name of just "global::TypeName" — no further '.' to split on — so the
        // "global::" prefix must be stripped explicitly before taking the last '.'-separated
        // segment, rather than assuming a namespace-qualified name is always present.
        const string globalPrefix = "global::";
        var unqualified = handlerType.StartsWith(globalPrefix, StringComparison.Ordinal)
            ? handlerType.Substring(globalPrefix.Length)
            : handlerType;
        var simpleName = unqualified.Split('.').Last().TrimStart('@');
        return char.ToLowerInvariant(simpleName[0]) + simpleName.Substring(1);
    }

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
        string[] requestParameterNames,
        Location location)
    {
        public string HandlerType { get; } = handlerType;

        public string RequestType { get; } = requestType;

        public string? ResultType { get; } = resultType;

        public HandlerKind Kind { get; } = kind;

        public string? Permission { get; } = permission;

        public string[] RequestParameterNames { get; } = requestParameterNames;

        public Location Location { get; } = location;
    }

    sealed class AuthorizerModel(string authorizerType, string requestType, Location location)
    {
        public string AuthorizerType { get; } = authorizerType;

        public string RequestType { get; } = requestType;

        public Location Location { get; } = location;
    }
}
