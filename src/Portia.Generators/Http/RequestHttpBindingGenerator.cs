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
            .WithTrackingName("PortiaHttpBindings")
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
        var configured = operation?.Arguments.Any(argument => argument.Parameter?.Name == "configure") == true;
        var constant = operation?.Arguments.FirstOrDefault(a => a.Parameter?.Name == "pattern")?.Value.ConstantValue;
        if (constant is not { HasValue: true, Value: string pattern })
            return Invalid(invocation, "the route must be a compile-time string constant");

        var optionalToken = HttpBindingShape.RouteTokenPattern.Matches(pattern).Cast<Match>()
            .FirstOrDefault(HttpBindingShape.IsOptionalRouteToken);
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

        CallAnalysis? Mapping(ParameterModel[] parameters, bool hasDefaultBinder)
        {
            var location = context.SemanticModel.GetInterceptableLocation(invocation);
            return location is null
                ? null
                : new CallAnalysis(new CallModel(
                    requestType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), requestType.Name,
                    resultType, kind, verb, parameters, InterceptableLocationModel.From(location),
                    DiagnosticLocation.From(invocation.GetLocation()), ContainingScope(invocation),
                    IsExcludedFromDescription(invocation), configured, hasDefaultBinder), null);
        }

        var primaryConstructor = HttpBindingShape.SinglePublicConstructor(requestType);
        if (primaryConstructor is null)
            return configured
                ? Mapping([], false)
                : Invalid(invocation, "the request must expose exactly one public constructor");
        if (!configured && HttpBindingShape.HasUnsetRequiredMembers(requestType, primaryConstructor))
        {
            return Invalid(invocation,
                "required members are not bound; bind them through constructor parameters or mark the constructor [SetsRequiredMembers]");
        }

        var routeTokens = HttpBindingShape.RouteTokenPattern.Matches(pattern)
            .Cast<Match>()
            .Select(m => m.Groups[1].Value)
            .ToArray();

        var bodyCapable = verb is "Post" or "Put" or "Patch";

        var parameters = primaryConstructor.Parameters
            .Select(parameter =>
            {
                var routeToken = routeTokens.FirstOrDefault(token =>
                    HttpBindingShape.Normalize(token) == HttpBindingShape.Normalize(parameter.Name));
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
                    HttpBindingShape.GetTextParseKind(underlying),
                    HttpBindingShape.IsSupportedParameter(parameter, true));
            })
            .ToArray();

        for (var i = 0; i < parameters.Length; i++)
        {
            var parameter = primaryConstructor.Parameters[i];
            if (!HttpBindingShape.IsSupportedParameter(parameter, parameters[i].Source != ParameterSource.Body))
            {
                return configured
                    ? Mapping([], false)
                    : Invalid(invocation,
                        $"parameter '{parameter.Name}' requires a supported scalar TryParse for route/query binding; move complex values into a JSON body");
            }
        }

        return Mapping(parameters, true);
    }

    static CallAnalysis Invalid(InvocationExpressionSyntax invocation, string reason) =>
        new(null, new HttpBindingDiagnostic(HttpBindingDiagnosticKind.UnsupportedBinding,
            DiagnosticLocation.From(invocation.GetLocation()), reason));

    static string DefaultValue(IParameterSymbol parameter, string typeName) => parameter.ExplicitDefaultValue is null
        ? "default!"
        : $"({typeName})({NonFiniteLiteral(parameter.ExplicitDefaultValue) ?? SymbolDisplay.FormatPrimitive(parameter.ExplicitDefaultValue, true, false)})";

    // FormatPrimitive renders a non-finite value as a bare NaN or Infinity, and a decimal without its 'm'
    // suffix, where a large value is not a valid integer literal; none of those is C#.
    static string? NonFiniteLiteral(object value) => value switch
    {
        decimal number => number.ToString(CultureInfo.InvariantCulture) + "m",
        double number when double.IsNaN(number) => "double.NaN",
        double number when double.IsPositiveInfinity(number) => "double.PositiveInfinity",
        double number when double.IsNegativeInfinity(number) => "double.NegativeInfinity",
        float number when float.IsNaN(number) => "float.NaN",
        float number when float.IsPositiveInfinity(number) => "float.PositiveInfinity",
        float number when float.IsNegativeInfinity(number) => "float.NegativeInfinity",
        _ => null
    };

    static string Literal(string value) => SymbolDisplay.FormatLiteral(value, true);

    // Whether an endpoint is described is a property of the endpoint, not of the order its
    // conventions were written in, so the whole builder chain is walked rather than only the call
    // directly attached to the mapping. An exclusion applied through a local — the mapping's own
    // result, or the group it was mapped on — counts too; anything further away is left to the
    // runtime document check.
    static bool IsExcludedFromDescription(InvocationExpressionSyntax invocation)
    {
        SyntaxNode current = invocation;
        for (; current.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax next } access;
             current = next)
        {
            if (access.Name.Identifier.ValueText == "ExcludeFromDescription")
                return true;
        }

        if (current.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax result }
            && ExcludesLocal(invocation, result.Identifier.ValueText))
        {
            return true;
        }

        return invocation.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver }
               && ExcludesLocal(invocation, receiver.Identifier.ValueText);
    }

    // Only calls in the same executable scope count: a local function or lambda has its own locals, and a
    // same-named local there is a different variable.
    static bool ExcludesLocal(InvocationExpressionSyntax invocation, string local)
    {
        var scope = ExecutableScope(invocation);
        return scope is not null && scope.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(candidate =>
            ExecutableScope(candidate) == scope &&
            candidate.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "ExcludeFromDescription",
                Expression: IdentifierNameSyntax target
            }
            && target.Identifier.ValueText == local);
    }

    static SyntaxNode? ExecutableScope(SyntaxNode node) => node.Ancestors().FirstOrDefault(ancestor =>
        ancestor is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or BaseMethodDeclarationSyntax
            or AccessorDeclarationSyntax or CompilationUnitSyntax);

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
            .AppendLine();
        _ = InterceptsLocationPolyfill.AppendTo(source, true)
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
        var formBindable = hasBody && bodyParameters.All(p => p.FormBindable);
        var configurationType = call.Kind is CallKind.Stream or CallKind.Sse
            ? $"global::Cntryl.Portia.PortiaStreamingEndpointConfiguration<{call.RequestTypeFullName}>"
            : $"global::Cntryl.Portia.PortiaEndpointConfiguration<{call.RequestTypeFullName}{(call.ResultType is not null ? ", " + call.ResultType : string.Empty)}>";

        _ = source
            .Append("    [global::System.Runtime.CompilerServices.InterceptsLocation(")
            .Append(call.Location.Version)
            .Append(", \"")
            .Append(call.Location.Data)
            .AppendLine("\")]")
            .Append("    public static global::Microsoft.AspNetCore.Builder.IEndpointConventionBuilder Call")
            .Append(index)
            .Append("(this global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app, string pattern")
            .Append(call.Configured
                ? $", global::System.Action<{configurationType}> configure"
                : string.Empty)
            .AppendLine(")")
            .AppendLine("    {")
            .AppendLine("        global::Cntryl.Portia.PortiaHttpBinding.RequireHttpServices(app);");
        if (call.Configured)
        {
            _ = source.Append("        var configuration = new ").Append(configurationType).AppendLine("();")
                .AppendLine("        configure(configuration);")
                .Append("        configuration.Validate(")
                .Append(call.HasDefaultBinder ? "false" : "true")
                .AppendLine(", new global::Cntryl.Portia.PortiaHttpMember[]")
                .AppendLine("        {");
            foreach (var parameter in call.Parameters)
            {
                _ = source.Append("            new(").Append(Literal(parameter.Name)).Append(", ")
                    .Append(Literal(parameter.Source.ToString().ToLowerInvariant())).Append(", ")
                    .Append(parameter.Source != ParameterSource.Body || parameter.FormBindable ? "true" : "false")
                    .AppendLine("),");
            }

            _ = source.AppendLine("        });");
        }

        _ = source
            // Mapping the handler as a RequestDelegate keeps RequestDelegateFactory — and the
            // reflection it needs to bind parameters — out of the consumer's AOT build. That
            // overload adds no MethodInfo, and ApiExplorer describes only endpoints that carry
            // one, so the handler's own metadata is supplied explicitly beside Portia's.
            .AppendLine("        global::Microsoft.AspNetCore.Http.RequestDelegate handler = Dispatch;")
            .Append("        var builder = app.MapMethods(pattern, new[] { \"").Append(call.Verb.ToUpperInvariant())
            .Append("\" }, handler)")
            .AppendLine()
            .Append("            .WithMetadata(handler.Method, ")
            .Append(formBindable ? "new global::Cntryl.Portia.PortiaFormBindableBody(), " : string.Empty)
            .Append("new global::Cntryl.Portia.PortiaOpenApiOperation(")
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

        _ = source.AppendLine("            }));");
        if (call.Configured)
            _ = source.AppendLine("        configuration.ApplyMetadata(app, builder);");
        _ = source.AppendLine("        return builder;").AppendLine();

        var extraParameters = call.Kind is CallKind.Queue
            ? ", global::Cntryl.Portia.IRequestQueuePublisher queue"
            : string.Empty;

        // Streaming results defer enumeration to a writer that maps authorization before
        // starting the response and owns the iterator through completion or cancellation.
        var returnType = call.Kind switch
        {
            CallKind.Stream or CallKind.Sse when call.Configured =>
                "global::System.Threading.Tasks.Task<global::Microsoft.AspNetCore.Http.IResult>",
            CallKind.Stream => "global::Microsoft.AspNetCore.Http.IResult",
            CallKind.Sse => "global::Microsoft.AspNetCore.Http.IResult",
            CallKind.Send or CallKind.Queue =>
                "global::System.Threading.Tasks.Task<global::Microsoft.AspNetCore.Http.IResult>",
            _ => throw new ArgumentOutOfRangeException(nameof(call))
        };
        var handlePrefix = call.Kind is CallKind.Stream or CallKind.Sse && !call.Configured ? string.Empty : "async ";
        var bindingFailure = call.Kind is CallKind.Stream or CallKind.Sse
            ? "throw new global::Microsoft.AspNetCore.Http.BadHttpRequestException(\"Malformed request.\");"
            : "return global::Cntryl.Portia.PortiaHttpBinding.Problem(400, \"Malformed request.\");";

        _ = source
            .AppendLine(
                "        async global::System.Threading.Tasks.Task Dispatch(global::Microsoft.AspNetCore.Http.HttpContext httpContext)")
            .AppendLine("        {")
            .AppendLine("            try")
            .AppendLine("            {");
        if (!string.Equals(call.Verb, "Get", StringComparison.Ordinal))
        {
            // A state-changing request is refused before binding reads anything from it.
            _ = source
                .AppendLine(
                    "                if (global::Cntryl.Portia.PortiaHttpBinding.RejectCrossOrigin(httpContext) is { } crossOrigin)")
                .AppendLine("                {")
                .AppendLine("                    await crossOrigin.ExecuteAsync(httpContext).ConfigureAwait(false);")
                .AppendLine("                    return;")
                .AppendLine("                }");
        }

        _ = source
            .AppendLine(
                "                var bus = global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<global::Cntryl.Portia.IRequestBus>(httpContext.RequestServices);");
        if (call.Kind == CallKind.Queue)
        {
            _ = source.AppendLine(
                "                var queue = global::Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<global::Cntryl.Portia.IRequestQueuePublisher>(httpContext.RequestServices);");
        }

        _ = source.Append("                var result = ");
        if (call.Kind is CallKind.Send or CallKind.Queue || call.Configured)
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
                "            catch (global::Cntryl.Portia.HttpUnsupportedMediaTypeException ex) when (!httpContext.Response.HasStarted)")
            .AppendLine("            {")
            .AppendLine(
                "                await global::Cntryl.Portia.PortiaHttpBinding.Problem(415, ex.Message).ExecuteAsync(httpContext).ConfigureAwait(false);")
            .AppendLine("            }")
            .AppendLine(
                "            catch (global::Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException) when (!httpContext.Response.HasStarted)")
            .AppendLine("            {")
            .AppendLine(
                "                await global::Cntryl.Portia.PortiaHttpBinding.Problem(400, \"Invalid antiforgery token.\").ExecuteAsync(httpContext).ConfigureAwait(false);")
            .AppendLine("            }")
            .AppendLine(
                "            catch (global::Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (!httpContext.Response.HasStarted)")
            .AppendLine("            {")
            .AppendLine(
                "                await global::Cntryl.Portia.PortiaHttpBinding.Problem(400, ex.Message).ExecuteAsync(httpContext).ConfigureAwait(false);")
            .AppendLine("            }")
            .AppendLine(
                "            catch (global::Cntryl.Portia.EventStreamConcurrencyException) when (!httpContext.Response.HasStarted)")
            .AppendLine("            {")
            .AppendLine(
                "                await global::Cntryl.Portia.ResultHttpExtensions.ToHttpResult(global::Cntryl.Portia.Result.Failure(new global::Cntryl.Portia.RequestError(global::Cntryl.Portia.RequestErrorKind.Conflict, \"The request conflicted with a concurrent update.\", true))).ExecuteAsync(httpContext).ConfigureAwait(false);")
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
        if (call.Configured)
        {
            _ = source.Append("            ").Append(call.RequestTypeFullName).AppendLine(" request;")
                .AppendLine("            if (configuration.Binder is not null)")
                .AppendLine("            {")
                .AppendLine("                try")
                .AppendLine("                {")
                .AppendLine("                    if (configuration.HasBody)")
                .AppendLine("                    {")
                .AppendLine(
                    "                        global::Cntryl.Portia.PortiaHttpBinding.EnsureBodyWithinLimit(httpContext);")
                .AppendLine(
                    "                        global::Microsoft.AspNetCore.Http.HttpRequestRewindExtensions.EnableBuffering(httpContext.Request);")
                .AppendLine("                        try")
                .AppendLine("                        {")
                .AppendLine(
                    "                            await httpContext.Request.Body.CopyToAsync(global::System.IO.Stream.Null, ct).ConfigureAwait(false);")
                .AppendLine("                        }")
                .AppendLine(
                    "                        catch (global::Microsoft.AspNetCore.Http.BadHttpRequestException ex) when (ex.StatusCode == 413)")
                .AppendLine("                        {")
                .AppendLine(
                    "                            throw new global::Cntryl.Portia.HttpPayloadTooLargeException();")
                .AppendLine("                        }")
                .AppendLine("                        httpContext.Request.Body.Position = 0;")
                .AppendLine("                    }")
                .AppendLine(
                    "                    request = await configuration.Binder(httpContext, ct).ConfigureAwait(false);")
                .AppendLine(
                    "                    if ((object?)request is null) throw new global::System.InvalidOperationException(\"OnBind returned null.\");")
                .AppendLine("                }")
                .AppendLine("                catch (global::System.Text.Json.JsonException)")
                .AppendLine("                {")
                .AppendLine(
                    "                    throw new global::Microsoft.AspNetCore.Http.BadHttpRequestException(\"Malformed request.\");")
                .AppendLine("                }")
                .AppendLine("                catch (global::System.FormatException)")
                .AppendLine("                {")
                .AppendLine(
                    "                    throw new global::Microsoft.AspNetCore.Http.BadHttpRequestException(\"Malformed request.\");")
                .AppendLine("                }")
                .AppendLine("                catch (global::System.OverflowException)")
                .AppendLine("                {")
                .AppendLine(
                    "                    throw new global::Microsoft.AspNetCore.Http.BadHttpRequestException(\"Malformed request.\");")
                .AppendLine("                }")
                .AppendLine("            }")
                .AppendLine("            else")
                .AppendLine("            {");
        }

        if (call.HasDefaultBinder)
        {
            foreach (var (parameter, i) in call.Parameters.Select((p, i) => (p, i)))
                _ = source.Append("            ").Append(parameter.Type).Append(" value").Append(i).AppendLine(";");
            _ = source.AppendLine("            try").AppendLine("            {");
            var jsonBody =
                "using var body = await global::Cntryl.Portia.PortiaHttpBinding.ReadJsonBodyAsync(httpContext, " +
                (bodyParameters.Any(parameter =>
                    !parameter.FormBindable && !parameter.Nullable && parameter.Default is null)
                    ? "true"
                    : "false") + ", ct).ConfigureAwait(false);";
            if (formBindable)
            {
                // Scalar-only bodies also accept HTML forms; the JSON wire names name the fields.
                _ = source.AppendLine(
                        "                if (global::Cntryl.Portia.PortiaHttpBinding.HasFormBody(httpContext))")
                    .AppendLine("                {")
                    .AppendLine(
                        "                    var form = await global::Cntryl.Portia.PortiaHttpBinding.ReadFormBodyAsync(httpContext, ct).ConfigureAwait(false);");
                AppendValues(true, "                    ");
                _ = source.AppendLine("                }")
                    .AppendLine("                else")
                    .AppendLine("                {")
                    .Append("                    ").AppendLine(jsonBody);
                AppendValues(false, "                    ");
                _ = source.AppendLine("                }");
            }
            else
            {
                if (hasBody)
                    _ = source.Append("                ").AppendLine(jsonBody);
                AppendValues(false, "                ");
            }

            void AppendValues(bool form, string indent)
            {
                foreach (var (parameter, i) in call.Parameters.Select((p, i) => (p, i)))
                {
                    var name = parameter.JsonName is not null
                        ? Literal(parameter.JsonName)
                        : $"(jsonOptions.PropertyNamingPolicy?.ConvertName({Literal(parameter.Name)}) ?? {Literal(parameter.Name)})";
                    var pad = indent;
                    var bodyRead = parameter.Source == ParameterSource.Body && !form;
                    // Binding cascades route, body, then query: a scalar member the body omits is
                    // looked up in the query string before its default or missing rule applies.
                    var cascade = bodyRead && parameter.FormBindable;
                    if (bodyRead)
                    {
                        if (cascade)
                        {
                            _ = source.Append(pad)
                                .Append(call.Configured ? $"if (!configuration.IsQueryMember({i}) && " : "if (")
                                .Append("global::Cntryl.Portia.PortiaHttpBinding.HasBodyMember<")
                                .Append(call.RequestTypeFullName).Append(", ").Append(parameter.Type)
                                .Append(">(body.RootElement, jsonOptions, ").Append(i).Append(", ").Append(name)
                                .AppendLine("))")
                                .Append(pad).AppendLine("{");
                        }

                        _ = source.Append(cascade ? pad + "    " : pad).Append("value").Append(i)
                            .Append(" = global::Cntryl.Portia.PortiaHttpBinding.ReadBody<")
                            .Append(call.RequestTypeFullName).Append(", ").Append(parameter.Type)
                            .Append(">(body.RootElement, jsonOptions, ").Append(i).Append(", ")
                            .Append(Literal(parameter.Name))
                            .Append(", ").Append(name)
                            .Append(parameter.Nullable ? ", true" : ", false")
                            .Append(parameter.Default is not null ? ", true, " : ", false, ")
                            .Append(parameter.Default ?? "default!").AppendLine(");");
                        if (!cascade)
                            continue;
                        _ = source.Append(pad).AppendLine("}").Append(pad).AppendLine("else").Append(pad)
                            .AppendLine("{");
                        pad += "    ";
                    }

                    var raw = parameter.Source switch
                    {
                        ParameterSource.Route =>
                            // Convert.ToString(null) returns an empty string; an absent route is still missing.
                            $"(httpContext.Request.RouteValues[{Literal(parameter.RouteToken!)}] is null ? null : global::System.Convert.ToString(httpContext.Request.RouteValues[{Literal(parameter.RouteToken!)}], global::System.Globalization.CultureInfo.InvariantCulture))",
                        // An empty form field is how a browser submits a blank non-text input.
                        ParameterSource.Body when form && IsBoolean(parameter.UnderlyingType) =>
                            (call.Configured
                                ? $"configuration.IsQueryMember({i}) ? global::Cntryl.Portia.PortiaHttpBinding.ReadQuery(httpContext, {name}) : "
                                : string.Empty) +
                            $"(global::Cntryl.Portia.PortiaHttpBinding.ReadFormBoolean(form, {name}) ?? global::Cntryl.Portia.PortiaHttpBinding.ReadQuery(httpContext, {name}))",
                        ParameterSource.Body when form =>
                            (call.Configured
                                ? $"configuration.IsQueryMember({i}) ? global::Cntryl.Portia.PortiaHttpBinding.ReadQuery(httpContext, {name}) : "
                                : string.Empty) +
                            $"(global::Cntryl.Portia.PortiaHttpBinding.ReadForm(form, {name}, {(IsString(parameter.UnderlyingType) ? "false" : "true")}) ?? global::Cntryl.Portia.PortiaHttpBinding.ReadQuery(httpContext, {name}))",
                        _ => $"global::Cntryl.Portia.PortiaHttpBinding.ReadQuery(httpContext, {name})"
                    };

                    // Browsers omit an unchecked checkbox, so an absent required form Boolean is false.
                    var formBoolean = form && parameter.Source == ParameterSource.Body &&
                                      IsBoolean(parameter.UnderlyingType);
                    _ = source.Append(pad).Append("var raw").Append(i).Append(" = ").Append(raw).AppendLine(";")
                        .Append(pad).Append("if (raw").Append(i).AppendLine(" is null)")
                        .Append(pad).AppendLine("{");
                    const string missing =
                        "throw new global::Microsoft.AspNetCore.Http.BadHttpRequestException(\"Missing required value.\");";
                    _ = parameter.Default is not null || parameter.Nullable
                        ? source.Append(pad).Append("    value").Append(i).Append(" = ")
                            .Append(parameter.Default ?? "default").AppendLine(";")
                        : formBoolean && call.Configured
                            ? source.Append(pad).Append("    if (configuration.IsQueryMember(").Append(i).Append(")) ")
                                .AppendLine(missing)
                                .Append(pad).Append("    value").Append(i).AppendLine(" = false;")
                            : formBoolean
                                ? source.Append(pad).Append("    value").Append(i).AppendLine(" = false;")
                                : source.Append(pad).Append("    ").AppendLine(missing);
                    _ = source.Append(pad).AppendLine("}").Append(pad).AppendLine("else")
                        .Append(pad).AppendLine("{");
                    if (IsString(parameter.UnderlyingType))
                    {
                        _ = source.Append(pad).Append("    value").Append(i).Append(" = raw").Append(i).AppendLine(";");
                    }
                    else
                    {
                        var parse = parameter.IsEnum
                            ? $"global::System.Enum.TryParse<{parameter.UnderlyingType}>(raw{i}, true, out var parsed{i})"
                            : IsBoolean(parameter.UnderlyingType) && form
                                ? $"global::Cntryl.Portia.PortiaHttpBinding.TryParseFormBoolean(raw{i}, out var parsed{i})"
                                : $"{parameter.UnderlyingType}.TryParse(raw{i}, " +
                                  (parameter.ParseKind == HttpBindingShape.TextParseKind.FormatProvider
                                      ? "global::System.Globalization.CultureInfo.InvariantCulture, "
                                      : "") + $"out var parsed{i})";
                        _ = source.Append(pad).Append("    if (!").Append(parse).AppendLine(")")
                            .Append(pad).AppendLine(
                                "        throw new global::Microsoft.AspNetCore.Http.BadHttpRequestException(\"Invalid scalar value.\");")
                            .Append(pad).Append("    value").Append(i).Append(" = parsed").Append(i).AppendLine(";");
                    }

                    _ = source.Append(pad).AppendLine("}");
                    if (cascade)
                        _ = source.Append(indent).AppendLine("}");
                }
            }

            _ = source.AppendLine("            }")
                .AppendLine("            catch (global::Cntryl.Portia.HttpPayloadTooLargeException)")
                .AppendLine("            {")
                .AppendLine("                throw;")
                .AppendLine("            }")
                .AppendLine("            catch (global::Cntryl.Portia.HttpUnsupportedMediaTypeException)")
                .AppendLine("            {")
                .AppendLine("                throw;")
                .AppendLine("            }")
                .AppendLine("            catch (global::System.Text.Json.JsonException)")
                .AppendLine("            {").Append("                ").AppendLine(bindingFailure)
                .AppendLine("            }")
                .AppendLine("            catch (global::Microsoft.AspNetCore.Http.BadHttpRequestException)")
                .AppendLine("            {").Append("                ").AppendLine(bindingFailure)
                .AppendLine("            }")
                .Append(call.Configured ? "                request = new " : "            var request = new ")
                .Append(call.RequestTypeFullName).Append('(')
                .Append(string.Join(", ", call.Parameters.Select((_, i) => "value" + i)))
                .AppendLine(");");
        }
        else if (call.Configured)
        {
            _ = source.AppendLine(
                "                throw new global::System.InvalidOperationException(\"This request shape requires OnBind.\");");
        }

        if (call.Configured)
            _ = source.AppendLine("            }");

        // The actor is never inferred beyond this point — httpContext.User is where "ambient"
        // stops and an explicit value starts, exactly like every other transport's call site.
        _ = source.AppendLine(
            "            var context = global::Cntryl.Portia.PortiaHttpBinding.CreateDispatchContext(httpContext);");

        if (call.Kind == CallKind.Queue)
        {
            // Accepting onto a durable queue is as consequential as running the request, so the
            // caller's authorization is settled here rather than at the worker: a 202 is final,
            // and deferring the refusal would turn an unauthorized call into a dead letter that
            // the caller never learns about. For the same reason an anonymous caller is answered
            // synchronously: the worker validates the carried credential again, and such a caller
            // has none to carry. A credential that cannot be carried is refused only after
            // authorization, so the status matches the synchronous path for a caller who lacks
            // permission.
            _ = source
                // A result hook shapes the response to the completed operation, so an endpoint that
                // declares one stays synchronous; honoring a preference is optional (RFC 7240).
                .Append("            if (")
                .Append(call.Configured ? "configuration.ResultHandler is null && " : string.Empty)
                .AppendLine("global::Cntryl.Portia.PortiaHttpBinding.PrefersRespondAsync(httpContext)")
                .AppendLine("                && global::Cntryl.Portia.PortiaHttpBinding.HasCredentials(httpContext))")
                .AppendLine("            {")
                .AppendLine(
                    "                var authorization = await bus.AuthorizeAsync(request, context, ct).ConfigureAwait(false);")
                .AppendLine("                if (!authorization.IsSuccess)")
                .AppendLine("                {")
                .AppendLine("                    return authorization.ToHttpResult();")
                .AppendLine("                }")
                .AppendLine()
                .AppendLine(
                    "                var actorToken = global::Cntryl.Portia.PortiaHttpBinding.ReadPortableBearerCredential(httpContext);")
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
                .Append("            var portiaResult = await bus.DispatchAsync")
                .Append(call.ResultType is null ? string.Empty : $"<{call.ResultType}>")
                .AppendLine("(request, context, ct).ConfigureAwait(false);")
                .Append(call.Configured
                    ? "            if (configuration.ResultHandler is not null)\n            {\n                var replacement = await configuration.ResultHandler(httpContext, portiaResult, ct).ConfigureAwait(false);\n                if (replacement is not null) return replacement;\n            }\n"
                    : string.Empty)
                .Append("            return portiaResult.ToHttpResult(")
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

    static bool IsBoolean(string type) => type is "bool" or "global::System.Boolean";

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
        HttpBindingShape.TextParseKind ParseKind,
        bool FormBindable);

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
        bool excludedFromDescription,
        bool configured,
        bool hasDefaultBinder)
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
        public bool Configured { get; } = configured;
        public bool HasDefaultBinder { get; } = hasDefaultBinder;

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
            && ExcludedFromDescription == other.ExcludedFromDescription
            && Configured == other.Configured
            && HasDefaultBinder == other.HasDefaultBinder;

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
                hash = (hash * 397) ^ ExcludedFromDescription.GetHashCode();
                hash = (hash * 397) ^ Configured.GetHashCode();
                return (hash * 397) ^ HasDefaultBinder.GetHashCode();
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
