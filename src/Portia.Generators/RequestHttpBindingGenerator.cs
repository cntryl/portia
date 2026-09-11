using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Cntryl.Portia;

/// <summary>
///     Intercepts every <c>app.MapPortiaGet/Post/Put/Patch/Delete&lt;TRequest[,TOut]&gt;(pattern)</c>
///     call site and replaces it with generated code that builds <c>TRequest</c> straight from the
///     route, query string, and JSON body — no attributes, no <c>BindAsync</c> on the request type, and
///     no ASP.NET Core reference required by whatever project declares <c>TRequest</c>. A primary
///     constructor parameter binds from the route if its name matches a <c>{token}</c> in the pattern;
///     otherwise it binds from the JSON body on POST/PUT/PATCH, or the query string on GET/DELETE.
///     This only works because interception happens in the compilation that calls the mapping method
///     (which already references ASP.NET Core, since it's calling an ASP.NET Core extension), and
///     <c>TRequest</c> is resolved purely through its symbol — its own source doesn't need to be part
///     of this compilation at all, so it's free to live in a plain class library.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class RequestHttpBindingGenerator : IIncrementalGenerator
{
    static readonly DiagnosticDescriptor UnsupportedBinding = new("PORTIA016", "Unsupported HTTP binding",
        "Cannot generate Portia HTTP binding: {0}", "Portia", DiagnosticSeverity.Error, true);

    static readonly DiagnosticDescriptor OptionalRoute = new("PORTIA026", "Optional route tokens are unsupported",
        "Portia route '{0}' contains optional token '{1}'; use a query parameter or separate endpoint", "Portia",
        DiagnosticSeverity.Error, true);

    static readonly DiagnosticDescriptor DuplicateOperationId = new("PORTIA027", "Duplicate OpenAPI operation ID",
        "Portia mappings produce duplicate operationId '{0}'", "Portia", DiagnosticSeverity.Error, true);

    static readonly Regex RouteTokenPattern =
        new(@"\{\*{0,2}([A-Za-z_][A-Za-z0-9_]*)(?:[:=?][^}]*)?\}", RegexOptions.Compiled);

    static readonly HashSet<string> MappingMethodNames =
    [
        "MapPortiaGet", "MapPortiaPost", "MapPortiaPut", "MapPortiaPatch", "MapPortiaDelete",
        "MapPortiaGetStream", "MapPortiaGetSse"
    ];

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var calls = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => IsCandidateInvocation(node),
                static (syntaxContext, _) => AnalyzeCall(syntaxContext))
            .Where(static call => call is not null)
            .Select(static (call, _) => call!)
            .Collect();

        context.RegisterSourceOutput(calls, static (sourceContext, calls) =>
        {
            foreach (var call in calls)
            {
                if (call.Diagnostic is { } diagnostic)
                    sourceContext.ReportDiagnostic(diagnostic.ToDiagnostic());
            }

            foreach (var duplicate in calls.Where(call => call.Model is not null).Select(call => call.Model!)
                         .Where(call => !call.ExcludedFromDescription)
                         .GroupBy(call => call.OperationId, StringComparer.Ordinal)
                         .Where(group =>
                             group.Select(call => call.RequestTypeFullName).Distinct(StringComparer.Ordinal).Count() > 1
                             || group.GroupBy(call => (call.RequestTypeFullName, call.ContainingSymbol),
                                     StringTupleComparer.Instance)
                                 .Any(sameRequestInScope => sameRequestInScope.Count() > 1)))
            {
                foreach (var call in duplicate)
                {
                    sourceContext.ReportDiagnostic(Diagnostic.Create(DuplicateOperationId,
                        call.DiagnosticLocation.ToLocation(), duplicate.Key));
                }
            }

            Generate(sourceContext, [.. calls.Where(call => call.Model is not null).Select(call => call.Model!)]);
        });
    }

    static bool IsCandidateInvocation(SyntaxNode node) =>
        node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.ValueText: var name } },
            ArgumentList.Arguments.Count: > 0
        }
        && MappingMethodNames.Contains(name);

    static CallAnalysis? AnalyzeCall(GeneratorSyntaxContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;

        if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
            return null;

        if (method.ContainingType.ToDisplayString() != "Cntryl.Portia.PortiaEndpointRouteBuilderExtensions"
            || method.TypeArguments.Length is not (1 or 2))
        {
            return null;
        }

        if (method.TypeArguments[0] is not INamedTypeSymbol requestType || !GeneratedTypeShape.IsSupported(requestType))
            return Invalid(invocation, "use an accessible, concrete, non-generic request type");

        var operation = context.SemanticModel.GetOperation(invocation) as IInvocationOperation;
        var constant = operation?.Arguments.FirstOrDefault(a => a.Parameter?.Name == "pattern")?.Value.ConstantValue;
        if (constant is not { HasValue: true, Value: string pattern })
            return Invalid(invocation, "the route must be a compile-time string constant");

        var optionalToken = RouteTokenPattern.Matches(pattern).Cast<Match>()
            .FirstOrDefault(match => match.Value.IndexOf('?') >= 0);
        if (optionalToken is not null)
        {
            return new CallAnalysis(null,
                new HttpBindingDiagnostic(HttpBindingDiagnosticKind.OptionalRoute,
                    DiagnosticLocation.From(invocation.GetLocation()), pattern, optionalToken.Groups[1].Value));
        }

        var verb = method.Name switch
        {
            "MapPortiaGet" or "MapPortiaGetStream" or "MapPortiaGetSse" => "Get",
            "MapPortiaPost" => "Post",
            "MapPortiaPut" => "Put",
            "MapPortiaPatch" => "Patch",
            "MapPortiaDelete" => "Delete",
            _ => null
        };

        if (verb == null)
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
            _ => CallKind.Send
        };

        if (kind == CallKind.Queue && method.TypeArguments.Length == 2)
        {
            return Invalid(invocation,
                "asynchronous queue dispatch requires a no-result IRequest; remove IQueuable from a result-bearing request");
        }

        var resultType = kind is CallKind.Queue
            ? null
            : method.TypeArguments.Length == 2
                ? method.TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                : null;

        var constructors = requestType.Constructors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public && !c.IsStatic).ToArray();
        if (constructors.Length != 1)
            return Invalid(invocation, "the request must expose exactly one public constructor");
        var primaryConstructor = constructors[0];

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
                    : bodyCapable
                        ? ParameterSource.Body
                        : ParameterSource.Query;

                var underlying = parameter.Type is INamedTypeSymbol
                {
                    OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
                } named
                    ? named.TypeArguments[0]
                    : parameter.Type;
                var property = requestType.GetMembers().OfType<IPropertySymbol>()
                    .FirstOrDefault(p => string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
                var jsonName = property?.GetAttributes().FirstOrDefault(a =>
                        a.AttributeClass?.ToDisplayString() ==
                        "System.Text.Json.Serialization.JsonPropertyNameAttribute")
                    ?.ConstructorArguments.FirstOrDefault().Value as string;
                var typeName = parameter.Type.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
                        SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                        SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
                return new ParameterModel(
                    parameter.Name, typeName,
                    underlying.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), source, routeToken,
                    parameter.NullableAnnotation == NullableAnnotation.Annotated ||
                    !SymbolEqualityComparer.Default.Equals(underlying, parameter.Type),
                    parameter.HasExplicitDefaultValue ? DefaultValue(parameter, typeName) : null,
                    jsonName,
                    underlying.TypeKind == TypeKind.Enum,
                    underlying.GetMembers("TryParse").OfType<IMethodSymbol>().Any(m => m.IsStatic &&
                        m.Parameters.Length == 3
                        && m.Parameters[1].Type.ToDisplayString() == "System.IFormatProvider"));
            })
            .ToArray();

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = primaryConstructor.Parameters[i];
            var type = parameter.Type is INamedTypeSymbol
            {
                OriginalDefinition.SpecialType: SpecialType.System_Nullable_T
            } nullable
                ? nullable.TypeArguments[0]
                : parameter.Type;
            if (parameter.RefKind != RefKind.None || (parameters[i].Source != ParameterSource.Body
                                                      && type.SpecialType != SpecialType.System_String &&
                                                      type.TypeKind != TypeKind.Enum
                                                      && !type.GetMembers("TryParse").OfType<IMethodSymbol>().Any(m =>
                                                          m.IsStatic && m.DeclaredAccessibility == Accessibility.Public
                                                                     && m.ReturnType.SpecialType ==
                                                                     SpecialType.System_Boolean &&
                                                                     m.Parameters.Length is 2 or 3
                                                                     && m.Parameters[0].Type.SpecialType ==
                                                                     SpecialType.System_String &&
                                                                     m.Parameters.Last().RefKind == RefKind.Out)))
            {
                return Invalid(invocation,
                    $"parameter '{parameter.Name}' requires a supported scalar TryParse for route/query binding; move complex values into a JSON body");
            }
        }

        var location = context.SemanticModel.GetInterceptableLocation(invocation);

        return location is null
            ? null
            : new CallAnalysis(new CallModel(
                requestType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                requestType.Name,
                resultType,
                kind,
                verb,
                parameters,
                InterceptableLocationModel.From(location),
                DiagnosticLocation.From(invocation.GetLocation()),
                ContainingScope(invocation),
                IsExcludedFromDescription(invocation)), null);
    }

    static CallAnalysis Invalid(InvocationExpressionSyntax invocation, string reason) =>
        new(null, new HttpBindingDiagnostic(HttpBindingDiagnosticKind.UnsupportedBinding,
            DiagnosticLocation.From(invocation.GetLocation()), reason));

    static string Normalize(string name) => name.Replace("_", string.Empty).ToLowerInvariant();

    static string DefaultValue(IParameterSymbol parameter, string typeName) => parameter.ExplicitDefaultValue is null
        ? "default!"
        : $"({typeName})({SymbolDisplay.FormatPrimitive(parameter.ExplicitDefaultValue, true, false)})";

    static string Literal(string value) => SymbolDisplay.FormatLiteral(value, true);

    // Whether an endpoint is described is a property of the endpoint, not of the order its
    // conventions were written in, so the whole builder chain is walked rather than only the call
    // directly attached to the mapping. A chain is still all this can see: an exclusion applied to
    // a variable later is not detected, and the runtime document check remains the backstop.
    static bool IsExcludedFromDescription(InvocationExpressionSyntax invocation)
    {
        for (SyntaxNode current = invocation;
             current.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax next } access;
             current = next)
        {
            if (access.Name.Identifier.ValueText == "ExcludeFromDescription")
                return true;
        }

        return false;
    }

    static string ContainingScope(InvocationExpressionSyntax invocation)
    {
        var declaration = invocation.Ancestors()
            .FirstOrDefault(node => node is LocalFunctionStatementSyntax or BaseMethodDeclarationSyntax);
        return declaration is null
            ? invocation.SyntaxTree.FilePath + ":top-level"
            : invocation.SyntaxTree.FilePath + ":" + declaration.SpanStart.ToString(CultureInfo.InvariantCulture);
    }

    static string CamelCase(string value)
    {
        if (value.Length == 0 || !char.IsUpper(value[0]))
            return value;
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (i == 1 && !char.IsUpper(chars[i]))
                break;
            var hasNext = i + 1 < chars.Length;
            if (i > 0 && hasNext && !char.IsUpper(chars[i + 1]))
                break;
            chars[i] = char.ToLowerInvariant(chars[i]);
        }

        return new string(chars);
    }

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
            .AppendLine(
                "    [global::System.AttributeUsage(global::System.AttributeTargets.Method, AllowMultiple = true)]")
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
            .Append("    public static global::Microsoft.AspNetCore.Builder.IEndpointConventionBuilder Call")
            .Append(index)
            .AppendLine("(this global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app, string pattern)")
            .AppendLine("    {")
            // Mapping the handler as a RequestDelegate keeps RequestDelegateFactory — and the
            // reflection it needs to bind parameters — out of the consumer's AOT build. That
            // overload adds no MethodInfo, and ApiExplorer describes only endpoints that carry
            // one, so the handler's own metadata is supplied explicitly beside Portia's.
            .AppendLine("        global::Microsoft.AspNetCore.Http.RequestDelegate handler = Dispatch;")
            .Append("        return app.MapMethods(pattern, new[] { \"").Append(call.Verb.ToUpperInvariant())
            .Append("\" }, handler)")
            .AppendLine()
            .Append("            .WithMetadata(handler.Method, new global::Cntryl.Portia.PortiaOpenApiOperation(")
            .Append(Literal(call.OperationId)).Append(", ")
            .Append(call.ResultType is null ? "null" : $"typeof({call.ResultType.TrimEnd('?')})").Append(", ")
            .Append(call.ResultType is null && call.Kind is CallKind.Send or CallKind.Queue ? "true" : "false")
            .Append(", ")
            .Append(call.Kind == CallKind.Stream ? "true" : "false").Append(", ")
            .Append(call.Kind == CallKind.Sse ? "true" : "false").Append(", ")
            .Append(call.Kind == CallKind.Queue ? "true" : "false")
            .AppendLine(", new global::Cntryl.Portia.PortiaOpenApiParameter[]")
            .AppendLine("            {");
        foreach (var parameter in call.Parameters)
        {
            var wireNameExpression = parameter.Source == ParameterSource.Route ? Literal(parameter.RouteToken!)
                : parameter.JsonName is not null ? Literal(parameter.JsonName) : "default(string)";
            _ = source.Append("                new(").Append(Literal(parameter.Name)).Append(", typeof(")
                .Append(parameter.Type.TrimEnd('?')).Append("), ")
                .Append(Literal(parameter.Source.ToString().ToLowerInvariant())).Append(", ")
                .Append(!parameter.Nullable && parameter.Default is null ? "true" : "false").Append(", ")
                .Append(parameter.Default is not null ? "true" : "false").Append(", ")
                .Append(parameter.Default ?? "null").Append(", ").Append(wireNameExpression).AppendLine("),");
        }

        _ = source.AppendLine("            }));")
            .AppendLine();

        var extraParameters = call.Kind is CallKind.Queue
            ? ", global::Cntryl.Portia.IRequestQueuePublisher queue"
            : string.Empty;

        // Streaming results defer enumeration to a writer that maps authorization before
        // starting the response and owns the iterator through completion or cancellation.
        var returnType = call.Kind switch
        {
            CallKind.Stream => "global::Microsoft.AspNetCore.Http.IResult",
            CallKind.Sse => "global::Microsoft.AspNetCore.Http.IResult",
            CallKind.Send or CallKind.Queue =>
                "global::System.Threading.Tasks.Task<global::Microsoft.AspNetCore.Http.IResult>",
            _ => throw new ArgumentOutOfRangeException(nameof(call))
        };
        var handlePrefix = call.Kind is CallKind.Stream or CallKind.Sse ? string.Empty : "async ";
        var bindingFailure = call.Kind is CallKind.Stream or CallKind.Sse
            ? "throw new global::Microsoft.AspNetCore.Http.BadHttpRequestException(\"Malformed request.\");"
            : "return global::Cntryl.Portia.PortiaHttpBinding.Problem(400, \"Malformed request.\");";

        _ = source
            .AppendLine(
                "        async global::System.Threading.Tasks.Task Dispatch(global::Microsoft.AspNetCore.Http.HttpContext httpContext)")
            .AppendLine("        {")
            .AppendLine("            try")
            .AppendLine("            {")
            .AppendLine(
                "                var bus = global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<global::Cntryl.Portia.IRequestBus>(httpContext.RequestServices);");
        if (call.Kind == CallKind.Queue)
        {
            _ = source.AppendLine(
                "                var queue = global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<global::Cntryl.Portia.IRequestQueuePublisher>(httpContext.RequestServices);");
        }

        _ = source.Append("                var result = ");
        if (call.Kind is CallKind.Send or CallKind.Queue)
            _ = source.Append("await ");

        _ = source.Append("Handle(httpContext, bus")
            .Append(call.Kind is CallKind.Queue ? ", queue" : string.Empty)
            .AppendLine(", httpContext.RequestAborted);")
            .AppendLine("                await result.ExecuteAsync(httpContext).ConfigureAwait(false);")
            .AppendLine("            }")
            .AppendLine(
                "            catch (global::Cntryl.Portia.HttpPayloadTooLargeException ex) when (!httpContext.Response.HasStarted)")
            .AppendLine("            {")
            .AppendLine(
                "                await global::Cntryl.Portia.PortiaHttpBinding.Problem(413, ex.Message).ExecuteAsync(httpContext).ConfigureAwait(false);")
            .AppendLine("            }")
            .AppendLine(
                "            catch (global::Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (!httpContext.Response.HasStarted)")
            .AppendLine("            {")
            .AppendLine(
                "                await global::Cntryl.Portia.PortiaHttpBinding.Problem(400, ex.Message).ExecuteAsync(httpContext).ConfigureAwait(false);")
            .AppendLine("            }")
            .AppendLine("            catch (global::System.Exception ex) when (!httpContext.Response.HasStarted)")
            .AppendLine("            {")
            .AppendLine(
                "                await global::Cntryl.Portia.PortiaHttpBinding.Unexpected(httpContext, ex).ExecuteAsync(httpContext).ConfigureAwait(false);")
            .AppendLine("            }")
            .AppendLine("            catch (global::System.Exception)")
            .AppendLine("            {")
            .AppendLine("                httpContext.Abort();")
            .AppendLine("            }")
            .AppendLine("        }")
            .AppendLine();

        _ = source
            .Append("        ").Append(handlePrefix).Append(returnType).Append(" Handle(")
            .Append("global::Microsoft.AspNetCore.Http.HttpContext httpContext, global::Cntryl.Portia.IRequestBus bus")
            .Append(extraParameters)
            .AppendLine(", global::System.Threading.CancellationToken ct)")
            .AppendLine("        {");

        _ = source.AppendLine(
            "            var jsonOptions = global::Cntryl.Portia.PortiaHttpBinding.GetJsonOptions(httpContext);");
        foreach (var (parameter, i) in call.Parameters.Select((p, i) => (p, i)))
            _ = source.Append("            ").Append(parameter.Type).Append(" value").Append(i).AppendLine(";");
        _ = source.AppendLine("            try").AppendLine("            {");
        if (hasBody)
        {
            _ = source.Append(
                    "                using var body = await global::Cntryl.Portia.PortiaHttpBinding.ReadJsonBodyAsync(httpContext, ")
                .Append(bodyParameters.Any(parameter => !parameter.Nullable && parameter.Default is null)
                    ? "true"
                    : "false")
                .AppendLine(", ct).ConfigureAwait(false);");
        }

        foreach (var (parameter, i) in call.Parameters.Select((p, i) => (p, i)))
        {
            var name = parameter.JsonName is not null
                ? Literal(parameter.JsonName)
                : $"(jsonOptions.PropertyNamingPolicy?.ConvertName({Literal(parameter.Name)}) ?? {Literal(parameter.Name)})";
            if (parameter.Source == ParameterSource.Body)
            {
                _ = source.Append("                value").Append(i)
                    .Append(" = global::Cntryl.Portia.PortiaHttpBinding.ReadBody<")
                    .Append(call.RequestTypeFullName).Append(", ").Append(parameter.Type)
                    .Append(">(body.RootElement, jsonOptions, ").Append(i).Append(", ").Append(Literal(parameter.Name))
                    .Append(", ").Append(name)
                    .Append(parameter.Nullable ? ", true" : ", false")
                    .Append(parameter.Default is not null ? ", true, " : ", false, ")
                    .Append(parameter.Default ?? "default!").AppendLine(");");
                continue;
            }

            var raw = parameter.Source == ParameterSource.Route
                ? $"global::System.Convert.ToString(httpContext.Request.RouteValues[{Literal(parameter.RouteToken!)}], global::System.Globalization.CultureInfo.InvariantCulture)"
                : $"global::Cntryl.Portia.PortiaHttpBinding.ReadQuery(httpContext, {name})";
            if (parameter.Source ==
                // Convert.ToString(null) returns an empty string; an absent route is still missing.
                ParameterSource.Route)
            {
                raw = $"(httpContext.Request.RouteValues[{Literal(parameter.RouteToken!)}] is null ? null : {raw})";
            }

            _ = source.Append("                var raw").Append(i).Append(" = ").Append(raw).AppendLine(";")
                .Append("                if (raw").Append(i).AppendLine(" is null)")
                .AppendLine("                {");
            _ = parameter.Default is not null || parameter.Nullable
                ? source.Append("                    value").Append(i).Append(" = ")
                    .Append(parameter.Default ?? "default").AppendLine(";")
                : source.AppendLine(
                    "                    throw new global::Microsoft.AspNetCore.Http.BadHttpRequestException(\"Missing required value.\");");
            _ = source.AppendLine("                }").AppendLine("                else")
                .AppendLine("                {");
            if (IsString(parameter.UnderlyingType))
            {
                _ = source.Append("                    value").Append(i).Append(" = raw").Append(i).AppendLine(";");
            }
            else
            {
                var parse = parameter.IsEnum
                    ? $"global::System.Enum.TryParse<{parameter.UnderlyingType}>(raw{i}, true, out var parsed{i})"
                    : $"{parameter.UnderlyingType}.TryParse(raw{i}, " +
                      (parameter.HasProviderParse
                          ? "global::System.Globalization.CultureInfo.InvariantCulture, "
                          : "") + $"out var parsed{i})";
                _ = source.Append("                    if (!").Append(parse).AppendLine(")")
                    .AppendLine(
                        "                        throw new global::Microsoft.AspNetCore.Http.BadHttpRequestException(\"Invalid scalar value.\");")
                    .Append("                    value").Append(i).Append(" = parsed").Append(i).AppendLine(";");
            }

            _ = source.AppendLine("                }");
        }

        _ = source.AppendLine("            }")
            .AppendLine("            catch (global::Cntryl.Portia.HttpPayloadTooLargeException)")
            .AppendLine("            {")
            .AppendLine("                throw;")
            .AppendLine("            }")
            .AppendLine("            catch (global::System.Text.Json.JsonException)")
            .AppendLine("            {").Append("                ").AppendLine(bindingFailure)
            .AppendLine("            }")
            .AppendLine("            catch (global::Microsoft.AspNetCore.Http.BadHttpRequestException)")
            .AppendLine("            {").Append("                ").AppendLine(bindingFailure)
            .AppendLine("            }")
            .Append("            var request = new ").Append(call.RequestTypeFullName).Append('(')
            .Append(string.Join(", ", call.Parameters.Select((_, i) => "value" + i)))
            .AppendLine(");");

        // The actor is never inferred beyond this point — httpContext.User is where "ambient"
        // stops and an explicit value starts, exactly like every other transport's call site.
        _ = source.AppendLine(
            "            var context = global::Cntryl.Portia.PortiaHttpBinding.CreateDispatchContext(httpContext);");

        if (call.Kind == CallKind.Queue)
        {
            // Accepting onto a durable queue is as consequential as running the request, so the
            // caller's authorization is settled here rather than at the worker: a 202 is final,
            // and deferring the refusal would turn an unauthorized call into a dead letter that
            // the caller never learns about.
            _ = source
                .AppendLine("            if (global::Cntryl.Portia.PortiaHttpBinding.PrefersRespondAsync(httpContext))")
                .AppendLine("            {")
                .AppendLine(
                    "                var authorization = await bus.AuthorizeAsync(request, context, ct).ConfigureAwait(false);")
                .AppendLine("                if (!authorization.IsSuccess)")
                .AppendLine("                {")
                .AppendLine("                    return authorization.ToHttpResult();")
                .AppendLine("                }")
                .AppendLine()
                .AppendLine(
                    "                var actorToken = global::Cntryl.Portia.PortiaHttpBinding.ReadBearerCredential(httpContext);")
                .AppendLine(
                    "                await queue.EnqueueAsync(request, global::Cntryl.Portia.PortiaHttpBinding.ResolveRouteValues(httpContext), actorToken, context.Metadata, ct).ConfigureAwait(false);")
                .AppendLine(
                    "                return global::Cntryl.Portia.PortiaHttpBinding.Accepted(httpContext, context.Metadata.RequestId);")
                .AppendLine("            }")
                .AppendLine();
        }

        _ = call.Kind switch
        {
            CallKind.Stream => source
                .Append("            return global::Cntryl.Portia.PortiaStreamResults.Json(bus.DispatchStreamAsync<")
                .Append(call.ResultType).AppendLine(">(request, context, ct));"),
            CallKind.Sse => source
                .Append("            return global::Cntryl.Portia.PortiaStreamResults.Sse(bus.DispatchStreamAsync<")
                .Append(call.ResultType).AppendLine(">(request, context, ct));"),
            CallKind.Send or CallKind.Queue => source
                .Append("            return (await bus.DispatchAsync")
                .Append(call.ResultType is null ? string.Empty : $"<{call.ResultType}>")
                .Append("(request, context, ct)).ToHttpResult(")
                .Append(call.ResultType is null
                    ? string.Empty
                    : $"(global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<{call.ResultType}>)jsonOptions.GetTypeInfo(typeof({call.ResultType}))")
                .AppendLine(");"),
            _ => throw new ArgumentOutOfRangeException(nameof(call))
        };

        _ = source
            .AppendLine("        }")
            .AppendLine("    }")
            .AppendLine();
    }

    static bool IsString(string type) =>
        type is "string" or "string?" or "global::System.String" or "global::System.String?";

    sealed record CallAnalysis(CallModel? Model, HttpBindingDiagnostic? Diagnostic);

    enum HttpBindingDiagnosticKind
    {
        OptionalRoute,
        UnsupportedBinding
    }

    sealed record HttpBindingDiagnostic(
        HttpBindingDiagnosticKind Kind,
        DiagnosticLocation Location,
        string FirstArgument,
        string? SecondArgument = null)
    {
        public Diagnostic ToDiagnostic() => Kind switch
        {
            HttpBindingDiagnosticKind.OptionalRoute =>
                Diagnostic.Create(OptionalRoute, Location.ToLocation(), FirstArgument, SecondArgument),
            HttpBindingDiagnosticKind.UnsupportedBinding =>
                Diagnostic.Create(UnsupportedBinding, Location.ToLocation(), FirstArgument),
            _ => throw new ArgumentOutOfRangeException(nameof(Kind))
        };
    }

    enum ParameterSource
    {
        Route,
        Body,
        Query
    }

    enum CallKind
    {
        Send,
        Queue,
        Stream,
        Sse
    }

    sealed record ParameterModel(
        string Name,
        string Type,
        string UnderlyingType,
        ParameterSource Source,
        string? RouteToken,
        bool Nullable,
        string? Default,
        string? JsonName,
        bool IsEnum,
        bool HasProviderParse);

    sealed class CallModel(
        string requestTypeFullName,
        string requestTypeName,
        string? resultType,
        CallKind kind,
        string verb,
        ParameterModel[] parameters,
        InterceptableLocationModel location,
        DiagnosticLocation diagnosticLocation,
        string containingSymbol,
        bool excludedFromDescription)
    {
        public string RequestTypeFullName { get; } = requestTypeFullName;

        public string RequestTypeName { get; } = requestTypeName;

        public string? ResultType { get; } = resultType;

        public string OperationId { get; } = CamelCase(requestTypeName);

        public CallKind Kind { get; } = kind;

        public string Verb { get; } = verb;

        public ParameterModel[] Parameters { get; } = parameters;

        public InterceptableLocationModel Location { get; } = location;

        public DiagnosticLocation DiagnosticLocation { get; } = diagnosticLocation;

        public string ContainingSymbol { get; } = containingSymbol;

        public bool ExcludedFromDescription { get; } = excludedFromDescription;

        public override bool Equals(object? obj) => obj is CallModel other && Equals(other);

        bool Equals(CallModel other) =>
            string.Equals(RequestTypeFullName, other.RequestTypeFullName, StringComparison.Ordinal)
            && string.Equals(RequestTypeName, other.RequestTypeName, StringComparison.Ordinal)
            && string.Equals(ResultType, other.ResultType, StringComparison.Ordinal)
            && Kind == other.Kind
            && string.Equals(Verb, other.Verb, StringComparison.Ordinal)
            && Parameters.SequenceEqual(other.Parameters)
            && Location == other.Location
            && DiagnosticLocation == other.DiagnosticLocation
            && string.Equals(ContainingSymbol, other.ContainingSymbol, StringComparison.Ordinal)
            && ExcludedFromDescription == other.ExcludedFromDescription;

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = StringComparer.Ordinal.GetHashCode(RequestTypeFullName);
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(RequestTypeName);
                hash = (hash * 397) ^ (ResultType is null ? 0 : StringComparer.Ordinal.GetHashCode(ResultType));
                hash = (hash * 397) ^ (int)Kind;
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(Verb);
                foreach (var parameter in Parameters)
                    hash = (hash * 397) ^ parameter.GetHashCode();
                hash = (hash * 397) ^ Location.GetHashCode();
                hash = (hash * 397) ^ DiagnosticLocation.GetHashCode();
                hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(ContainingSymbol);
                return (hash * 397) ^ ExcludedFromDescription.GetHashCode();
            }
        }
    }

    sealed class StringTupleComparer : IEqualityComparer<(string RequestTypeFullName, string ContainingSymbol)>
    {
        public static StringTupleComparer Instance { get; } = new();

        public bool Equals((string RequestTypeFullName, string ContainingSymbol) x,
            (string RequestTypeFullName, string ContainingSymbol) y) =>
            StringComparer.Ordinal.Equals(x.RequestTypeFullName, y.RequestTypeFullName)
            && StringComparer.Ordinal.Equals(x.ContainingSymbol, y.ContainingSymbol);

        public int GetHashCode((string RequestTypeFullName, string ContainingSymbol) obj)
        {
            unchecked
            {
                return (StringComparer.Ordinal.GetHashCode(obj.RequestTypeFullName) * 397)
                       ^ StringComparer.Ordinal.GetHashCode(obj.ContainingSymbol);
            }
        }
    }
}
