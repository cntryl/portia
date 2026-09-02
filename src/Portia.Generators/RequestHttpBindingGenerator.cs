using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia;

/// <summary>
/// Intercepts every <c>app.MapPortiaGet/Post/Put/Patch/Delete[Async]&lt;TRequest[,TOut]&gt;(pattern)</c>
/// call site and replaces it with generated code that builds <c>TRequest</c> straight from the
/// route, query string, and JSON body — no attributes, no <c>BindAsync</c> on the request type, and
/// no ASP.NET Core reference required by whatever project declares <c>TRequest</c>. A primary
/// constructor parameter binds from the route if its name matches a <c>{token}</c> in the pattern;
/// otherwise it binds from the JSON body on POST/PUT/PATCH, or the query string on GET/DELETE.
///
/// This only works because interception happens in the compilation that calls the mapping method
/// (which already references ASP.NET Core, since it's calling an ASP.NET Core extension), and
/// <c>TRequest</c> is resolved purely through its symbol — its own source doesn't need to be part
/// of this compilation at all, so it's free to live in a plain class library.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class RequestHttpBindingGenerator : IIncrementalGenerator
{
    static readonly Regex RouteTokenPattern = new(@"\{([A-Za-z_][A-Za-z0-9_]*)(?::[^}]*)?\}", RegexOptions.Compiled);

    static readonly HashSet<string> MappingMethodNames =
    [
        "MapPortiaGet", "MapPortiaPost", "MapPortiaPut", "MapPortiaPatch", "MapPortiaDelete",
        "MapPortiaGetStream", "MapPortiaGetSse",
    ];

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var calls = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => IsCandidateInvocation(node),
                static (syntaxContext, _) => GetCallModel(syntaxContext))
            .Where(static call => call is not null)
            .Select(static (call, _) => call!)
            .Collect();

        context.RegisterSourceOutput(calls, static (sourceContext, calls) => Generate(sourceContext, calls));
    }

    static bool IsCandidateInvocation(SyntaxNode node) =>
        node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.ValueText: var name } },
            ArgumentList.Arguments.Count: > 0,
        } invocation
        && invocation.ArgumentList.Arguments[0].Expression.IsKind(SyntaxKind.StringLiteralExpression)
        && MappingMethodNames.Contains(name);

    static CallModel? GetCallModel(GeneratorSyntaxContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
            return null;

        if (method.TypeArguments.Length is not (1 or 2))
            return null;

        if (method.TypeArguments[0] is not INamedTypeSymbol requestType)
            return null;

        var pattern = (string)((LiteralExpressionSyntax)((InvocationExpressionSyntax)context.Node)
            .ArgumentList.Arguments[0].Expression).Token.Value!;

        var verb = method.Name switch
        {
            "MapPortiaGet" or "MapPortiaGetStream" or "MapPortiaGetSse" => "Get",
            "MapPortiaPost" => "Post",
            "MapPortiaPut" => "Put",
            "MapPortiaPatch" => "Patch",
            "MapPortiaDelete" => "Delete",
            _ => null,
        };

        if (verb is null)
            return null;

        // A mutating verb whose request also opted into IQueuable automatically gets the
        // Prefer-header enqueue pivot — there's no separate "Async"-suffixed method to remember;
        // the same MapPortiaPost/Put/Patch/Delete call just behaves differently based on what the
        // request itself declared, exactly like every other transport marker in this framework.
        var isQueuable = requestType.AllInterfaces.Any(iface => iface.ToDisplayString() == "Cntryl.Portia.IQueuable");

        var kind = method.Name switch
        {
            "MapPortiaGetStream" => CallKind.Stream,
            "MapPortiaGetSse" => CallKind.Sse,
            _ when verb != "Get" && isQueuable => CallKind.Queue,
            _ => CallKind.Send,
        };

        var resultType = kind is CallKind.Queue
            ? null
            : method.TypeArguments.Length == 2
                ? method.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                : null;

        var primaryConstructor = requestType.Constructors
            .FirstOrDefault(ctor => ctor.Parameters.Length > 0 && ctor.DeclaredAccessibility == Accessibility.Public);

        if (primaryConstructor is null && requestType.Constructors.Any(c => c.Parameters.Length == 0))
        {
            // A parameterless request (e.g. `Ping`) has nothing to bind — the interceptor still
            // fires, it just skips straight to constructing the request with no arguments.
            primaryConstructor = requestType.Constructors.First(c => c.Parameters.Length == 0);
        }

        if (primaryConstructor is null)
            return null;

        var routeTokens = RouteTokenPattern.Matches(pattern)
            .Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .ToArray();

        var bodyCapable = verb is "Post" or "Put" or "Patch";

        var parameters = primaryConstructor.Parameters
            .Select(parameter =>
            {
                var routeToken = routeTokens.FirstOrDefault(token => Normalize(token) == Normalize(parameter.Name));
                var source = routeToken is not null
                    ? ParameterSource.Route
                    : bodyCapable ? ParameterSource.Body : ParameterSource.Query;

                return new ParameterModel(
                    parameter.Name,
                    parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    source,
                    routeToken,
                    HasTryParse(parameter.Type),
                    parameter.Type.IsValueType);
            })
            .ToArray();

        var location = context.SemanticModel.GetInterceptableLocation(invocation);

        return location is null
            ? null
            : new CallModel(
                requestType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                requestType.Name,
                resultType,
                kind,
                verb,
                parameters,
                location);
    }

    static string Normalize(string name) => name.Replace("_", string.Empty).ToLowerInvariant();

    // Route and query values only ever carry strings, so a scalar TryParse(string, out T) is the
    // whole story there. A body property has no such restriction — a nested object or collection
    // has no meaningful TryParse at all — so this decides, per body parameter, whether generated
    // code can stay on the fast, reflection-free TryParse path or needs to fall back to a normal
    // JsonElement.Deserialize<T>() call (see AppendInterceptor), which honors [JsonConverter] and
    // handles arbitrary shapes the same way response serialization already does.
    static bool HasTryParse(ITypeSymbol type) =>
        type.GetMembers("TryParse").OfType<IMethodSymbol>().Any(method =>
            method.IsStatic
            && method.Parameters.Length == 2
            && method.Parameters[0].Type.SpecialType == SpecialType.System_String
            && method.Parameters[1].RefKind == RefKind.Out);

    static void Generate(SourceProductionContext context, ImmutableArray<CallModel> calls)
    {
        if (calls.IsDefaultOrEmpty)
            return;

        var source = new StringBuilder()
            .AppendLine("// <auto-generated />")
            .AppendLine("#nullable enable")
            .AppendLine("using Microsoft.AspNetCore.Builder;")
            .AppendLine("using Microsoft.AspNetCore.Http;")
            .AppendLine("using Microsoft.AspNetCore.Routing;")
            .AppendLine()
            // The BCL doesn't ship this attribute on every target yet; the compiler recognizes it
            // structurally by name, so a self-declared copy works exactly like the real one.
            .AppendLine("namespace System.Runtime.CompilerServices")
            .AppendLine("{")
            .AppendLine("    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]")
            .AppendLine("    file sealed class InterceptsLocationAttribute : global::System.Attribute")
            .AppendLine("    {")
            .AppendLine("        public InterceptsLocationAttribute(int version, string data)")
            .AppendLine("        {")
            .AppendLine("            _ = version;")
            .AppendLine("            _ = data;")
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine("}")
            .AppendLine()
            .AppendLine("namespace Cntryl.Portia.Generated")
            .AppendLine("{");

        // Body values are read directly off a JsonDocument in each interceptor below (see
        // AppendInterceptor) rather than through a source-generated JsonSerializerContext: the
        // System.Text.Json generator only sees the original user syntax trees, never types this
        // generator adds during the same pass, so a context declared here would never get filled in.
        // PortiaHttpJson.Options is only needed for the (rarer) complex-body-property fallback —
        // internal rather than file-scoped since it's referenced from the file-scoped interceptors
        // class below, and file-scoped types can't be seen from outside their own file anyway.
        _ = source
            .AppendLine("internal static class PortiaHttpJson")
            .AppendLine("{")
            .AppendLine("    internal static readonly global::System.Text.Json.JsonSerializerOptions Options = new(global::System.Text.Json.JsonSerializerDefaults.Web)")
            .AppendLine("    {")
            .AppendLine("        PropertyNamingPolicy = global::System.Text.Json.JsonNamingPolicy.SnakeCaseLower,")
            .AppendLine("    };")
            .AppendLine("}");

        _ = source.AppendLine("file static class PortiaHttpInterceptors").AppendLine("{");

        for (var i = 0; i < calls.Length; i++)
            AppendInterceptor(source, calls[i], i);

        _ = source.AppendLine("}").AppendLine("}");

        context.AddSource("PortiaGeneratedHttpBinding.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    static void AppendInterceptor(StringBuilder source, CallModel call, int index)
    {
        var bodyParameters = call.Parameters.Where(p => p.Source == ParameterSource.Body).ToArray();
        var hasBody = bodyParameters.Length > 0;

        _ = source
            .Append("    [global::System.Runtime.CompilerServices.InterceptsLocation(")
            .Append(call.Location.Version)
            .Append(", \"")
            .Append(call.Location.Data)
            .AppendLine("\")]")
            .Append("    public static global::Microsoft.AspNetCore.Builder.RouteHandlerBuilder Call")
            .Append(index)
            .AppendLine("(this global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app, string pattern)")
            .AppendLine("    {")
            .Append("        return app.Map").Append(call.Verb).AppendLine("(pattern, Handle);")
            .AppendLine();

        var extraParameters = call.Kind is CallKind.Queue
            ? ", global::Cntryl.Portia.IRequestQueuePublisher queue"
            : string.Empty;

        // Send/Queue dispatch through IRequestBus.SendAsync, which is awaited and wrapped in an
        // IResult; Stream/Sse dispatch through StreamAsync, which returns its IAsyncEnumerable
        // synchronously — ASP.NET Core's own minimal-API plumbing does the streaming from there.
        var returnType = call.Kind switch
        {
            CallKind.Stream => $"global::System.Collections.Generic.IAsyncEnumerable<{call.ResultType}>",
            CallKind.Sse => $"global::Microsoft.AspNetCore.Http.HttpResults.ServerSentEventsResult<{call.ResultType}>",
            CallKind.Send or CallKind.Queue => "global::System.Threading.Tasks.Task<global::Microsoft.AspNetCore.Http.IResult>",
            _ => throw new ArgumentOutOfRangeException(nameof(call)),
        };
        var handlePrefix = call.Kind is CallKind.Stream or CallKind.Sse ? string.Empty : "async ";
        var bindingFailure = call.Kind is CallKind.Stream or CallKind.Sse
            ? "throw new global::Microsoft.AspNetCore.Http.BadHttpRequestException(\"Malformed request.\");"
            : "return global::Microsoft.AspNetCore.Http.Results.BadRequest();";

        _ = source
            .Append("        ").Append(handlePrefix).Append(returnType).Append(" Handle(")
            .Append("global::Microsoft.AspNetCore.Http.HttpContext httpContext, global::Cntryl.Portia.IRequestBus bus")
            .Append(extraParameters)
            .AppendLine(", global::System.Threading.CancellationToken ct)")
            .AppendLine("        {");

        // Body parsed once, up front, via JsonDocument — a low-level, allocation-light parse with
        // no reflection and no source-generated JsonSerializerContext (which can't see types this
        // very generator adds, since generators don't see each other's output in the same pass).
        // Every parameter, whatever its source, ultimately resolves to a raw string that's handed
        // to the same TypeName.TryParse(...) convention already used for route and query values.
        if (hasBody)
        {
            _ = source
                .AppendLine("            global::System.Text.Json.JsonDocument? bodyDoc;")
                .AppendLine("            try")
                .AppendLine("            {")
                .AppendLine("                bodyDoc = await global::System.Text.Json.JsonDocument.ParseAsync(httpContext.Request.Body, cancellationToken: ct).ConfigureAwait(false);")
                .AppendLine("            }")
                .AppendLine("            catch (global::System.Text.Json.JsonException)")
                .AppendLine("            {")
                .AppendLine("                return global::Microsoft.AspNetCore.Http.Results.BadRequest();")
                .AppendLine("            }")
                .AppendLine()
                .AppendLine("            using var body = bodyDoc;")
                .AppendLine();
        }

        foreach (var parameter in call.Parameters)
        {
            if (parameter.Source == ParameterSource.Body && !parameter.HasTryParse && !IsString(parameter.Type))
            {
                // No TryParse to lean on and it's not a plain string — a nested object, a
                // collection, or any other shape with no meaningful raw-string form. Falls back
                // to a normal JsonElement.Deserialize<T>() for just this one property, which (unlike
                // the TryParse path) honors [JsonConverter] the same way response serialization
                // already does, and handles arbitrary shapes rather than only flat scalars.
                var propertyName = ToSnakeCase(parameter.Name);
                var propVariable = parameter.Name + "Prop";

                // JsonElement.Deserialize<TValue>() returns TValue? — for a reference type
                // that's just TValue with a nullable annotation, but for a value type (a record
                // struct with no TryParse, say) it's genuinely Nullable<TValue>, which has no
                // implicit conversion to TValue. Deserializing into a distinctly-named nullable
                // local first, then unwrapping with .Value after the null check, handles both
                // shapes with the same generated structure either way.
                var deserializeTarget = parameter.IsValueType ? parameter.Name + "Nullable" : parameter.Name;
                var deserializeType = parameter.IsValueType ? parameter.Type + "?" : parameter.Type;

                _ = source
                    .Append("            if (!body.RootElement.TryGetProperty(\"").Append(propertyName).Append("\", out var ").Append(propVariable).AppendLine("))")
                    .Append("                ").AppendLine(bindingFailure)
                    .Append("            ").Append(deserializeType).Append(' ').Append(deserializeTarget).AppendLine(";")
                    .AppendLine("            try")
                    .AppendLine("            {")
                    .Append("                ").Append(deserializeTarget).Append(" = global::System.Text.Json.JsonSerializer.Deserialize<").Append(parameter.Type).Append(">(")
                    .Append(propVariable).AppendLine(", global::Cntryl.Portia.Generated.PortiaHttpJson.Options)!;")
                    .AppendLine("            }")
                    // A property's JSON not matching its target shape (e.g. a string where an
                    // object or array is expected) throws here — a client-facing 400, exactly
                    // like every other binding failure above, not an unhandled 500 that would
                    // otherwise send a caller straight into Portia's own generated code to
                    // understand what went wrong.
                    .AppendLine("            catch (global::System.Text.Json.JsonException)")
                    .AppendLine("            {")
                    .Append("                ").AppendLine(bindingFailure)
                    .AppendLine("            }")
                    .Append("            if (").Append(deserializeTarget).AppendLine(" is null)")
                    .Append("                ").AppendLine(bindingFailure);

                if (parameter.IsValueType)
                {
                    _ = source.Append("            var ").Append(parameter.Name).Append(" = ").Append(deserializeTarget).AppendLine(".Value;");
                }

                continue;
            }

            var rawValue = parameter.Source switch
            {
                ParameterSource.Route => $"httpContext.Request.RouteValues[\"{parameter.RouteToken}\"]?.ToString()",
                ParameterSource.Query => $"httpContext.Request.Query[\"{ToSnakeCase(parameter.Name)}\"].ToString()",
                ParameterSource.Body => null,
                _ => null,
            };

            if (rawValue is null)
            {
                var propertyName = ToSnakeCase(parameter.Name);
                var propVariable = parameter.Name + "Prop";
                var rawVariable = parameter.Name + "Raw";
                _ = source
                    .Append("            if (!body.RootElement.TryGetProperty(\"").Append(propertyName).Append("\", out var ").Append(propVariable).AppendLine("))")
                    .Append("                ").AppendLine(bindingFailure)
                    .Append("            var ").Append(rawVariable).Append(" = ").Append(propVariable)
                    .AppendLine(".ValueKind == global::System.Text.Json.JsonValueKind.String")
                    .Append("                ? ").Append(propVariable).Append(".GetString()")
                    .Append("                : ").Append(propVariable).AppendLine(".GetRawText();");
                rawValue = rawVariable;
            }

            if (IsString(parameter.Type))
            {
                if (parameter.Type.EndsWith("?", StringComparison.Ordinal))
                {
                    _ = source.Append("            var ").Append(parameter.Name).Append(" = ").Append(rawValue).AppendLine(";");
                    continue;
                }

                _ = source
                    .Append("            if (").Append(rawValue).Append(" is not { } ").Append(parameter.Name).AppendLine("Str)")
                    .Append("                ").AppendLine(bindingFailure)
                    .Append("            var ").Append(parameter.Name).Append(" = ").Append(parameter.Name).AppendLine("Str;");
                continue;
            }

            _ = source
                .Append("            if (!").Append(parameter.Type).Append(".TryParse(").Append(rawValue)
                .Append(", out var ").Append(parameter.Name).AppendLine("))")
                .Append("                ").AppendLine(bindingFailure);
        }

        _ = source
            .Append("            var request = new ").Append(call.RequestTypeFullName).Append('(')
            .Append(string.Join(", ", call.Parameters.Select(p => p.Name)))
            .AppendLine(");");

        // The actor is never inferred beyond this point — httpContext.User is where "ambient"
        // stops and an explicit value starts, exactly like every other transport's call site.
        _ = source.AppendLine("            var actor = httpContext.User;");

        if (call.Kind is CallKind.Queue)
        {
            _ = source
                .AppendLine("            if (httpContext.Request.Headers.TryGetValue(\"Prefer\", out var prefer)")
                .AppendLine("                && prefer.Any(value => value != null && value.Contains(\"respond-async\", global::System.StringComparison.OrdinalIgnoreCase)))")
                .AppendLine("            {")
                .AppendLine("                var actorToken = httpContext.Request.Headers.Authorization.ToString() is { Length: > 0 } authHeader")
                .AppendLine("                    ? authHeader.StartsWith(\"Bearer \", global::System.StringComparison.OrdinalIgnoreCase) ? authHeader.Substring(7) : authHeader")
                .AppendLine("                    : null;")
                .AppendLine("                await queue.EnqueueAsync(request, global::Cntryl.Portia.RequestRouteValues.None, actorToken, ct).ConfigureAwait(false);")
                .AppendLine("                return global::Microsoft.AspNetCore.Http.Results.Accepted();")
                .AppendLine("            }")
                .AppendLine();
        }

        _ = call.Kind switch
        {
            CallKind.Stream => source.Append("            return bus.StreamAsync<").Append(call.ResultType).AppendLine(">(request, actor, ct);"),
            CallKind.Sse => source.Append("            return global::Microsoft.AspNetCore.Http.TypedResults.ServerSentEvents(bus.StreamAsync<").Append(call.ResultType).AppendLine(">(request, actor, ct));"),
            CallKind.Send or CallKind.Queue => source
                .Append("            return (await bus.SendAsync")
                .Append(call.ResultType is null ? string.Empty : $"<{call.ResultType}>")
                .AppendLine("(request, actor, ct)).ToHttpResult();"),
            _ => throw new ArgumentOutOfRangeException(nameof(call)),
        };

        _ = source
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine();
    }

    static bool IsString(string type) =>
        type is "string" or "string?" or "global::System.String" or "global::System.String?";

    // Mirrors JsonNamingPolicy.SnakeCaseLower so query keys line up with the JSON body's wire
    // casing (e.g. "includeArchived" -> "include_archived").
    static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 4);

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];

            if (char.IsUpper(c) && i > 0)
                _ = builder.Append('_');

            _ = builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    enum ParameterSource
    {
        Route,
        Body,
        Query,
    }

    enum CallKind
    {
        Send,
        Queue,
        Stream,
        Sse,
    }

    sealed class ParameterModel(string name, string type, ParameterSource source, string? routeToken, bool hasTryParse, bool isValueType)
    {
        public string Name { get; } = name;

        public string Type { get; } = type;

        public ParameterSource Source { get; } = source;

        public string? RouteToken { get; } = routeToken;

        public bool HasTryParse { get; } = hasTryParse;

        // JsonElement.Deserialize<TValue>() returns TValue?, which for a value type means
        // Nullable<TValue> — the fallback JSON path below has to declare and null-check through
        // that nullable shape, then unwrap with .Value, rather than assume the reference-type
        // shape (where TValue? just means TValue, already nullable) works for both.
        public bool IsValueType { get; } = isValueType;
    }

    sealed class CallModel(
        string requestTypeFullName,
        string requestTypeName,
        string? resultType,
        CallKind kind,
        string verb,
        ParameterModel[] parameters,
        InterceptableLocation location)
    {
        public string RequestTypeFullName { get; } = requestTypeFullName;

        public string RequestTypeName { get; } = requestTypeName;

        public string? ResultType { get; } = resultType;

        public CallKind Kind { get; } = kind;

        public string Verb { get; } = verb;

        public ParameterModel[] Parameters { get; } = parameters;

        public InterceptableLocation Location { get; } = location;
    }
}
