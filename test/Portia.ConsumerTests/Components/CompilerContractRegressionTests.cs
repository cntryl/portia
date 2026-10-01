using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Cntryl.Portia.Consumer;

public sealed class CompilerContractRegressionTests
{
    [Theory]
    [InlineData("Command", "_ = value.ExpectDenied();")]
    [InlineData("Query", "_ = value.ExpectDenied();")]
    [InlineData("Feed", "_ = value.ExpectDenied();")]
    [InlineData("Command", "Action run = () => value.ExpectDenied(); run();")]
    [InlineData("Query", "Action run = () => (value.ExpectDenied()); run();")]
    [InlineData("Feed", "Action run = () => value.ExpectDenied(); run();")]
    public void DiagnosesDiscardedAugmentationEvenWhenOriginalIsAwaited(string request, string discard)
    {
        var source = $$"""
            using System;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Cntryl.Portia.Testing;
            public sealed record Command : IRequest;
            public sealed record Query : IRequest<string>;
            public sealed record Feed : IStreamRequest<string>;
            public static class Tests
            {
                public static async Task Run(IServiceProvider services)
                {
                    var value = RequestScenario.For(services).When(new {{request}}());
                    {{discard}}
                    await value;
                }
            }
            """;
        var diagnostic = Assert.Single(GeneratorCompilation.Diagnostics(source, new ScenarioObservationAnalyzer()), d => d.Id == "PORTIA107");
        Assert.Equal("DiagnosticScenario.cs", diagnostic.Location.GetLineSpan().Path);
        Assert.Contains("assert", diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("namespace App_A {", "}", "namespace App {", "A_Context")]
    [InlineData("public class App_A {", "}", "public class App {", "A_Context")]
    [InlineData("namespace @App_A {", "}", "namespace @App {", "@A_Context")]
    public void DistinctContextIdentitiesCompile(string firstContainer, string end, string secondContainer, string secondName)
    {
        var source = ContextUsings + "public sealed record Payload;" +
            firstContainer + ManualContext("Context") + end +
            secondContainer + ManualContext(secondName) + end;
        var diagnostics = GeneratorCompilation.OutputDiagnostics(source, new JsonRootMarkerGenerator());
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("public class Outer {", "private", "Context", "public", "", "}", "visible")]
    [InlineData("private class Outer {", "internal", "Context", "public", "", "}", "visible")]
    [InlineData("", "file", "Context", "public", "", "", "visible")]
    [InlineData("", "public", "Context<T>", "public", "", "", "generic")]
    [InlineData("public class Outer<T> {", "internal", "Context", "public", "", "}", "generic")]
    [InlineData("", "public abstract", "Context", "public", "", "", "concrete")]
    [InlineData("", "public", "Context", "private", "", "", "constructor")]
    [InlineData("", "public", "Context", "protected", "", "", "constructor")]
    [InlineData("", "public", "Context", "public", "missing", "", "constructor")]
    public void DiagnosesInvalidContextWithoutInvalidGeneratedReferences(string prefix, string visibility, string name,
        string constructorVisibility, string variant, string suffix, string requirement)
    {
        // A private container is nested so the application declaration itself is legal.
        if (prefix.StartsWith("private", StringComparison.Ordinal))
        { prefix = "public class Enclosing { " + prefix; suffix += "}"; }
        var source = ContextUsings + "public sealed record Payload;" + prefix +
            ManualContext(name, visibility, constructorVisibility, variant == "missing") + suffix;
        var diagnostics = GeneratorCompilation.OutputDiagnostics(source, new JsonRootMarkerGenerator(), new RegistrationCallInterceptorGenerator());
        var diagnostic = Assert.Single(diagnostics, d => d.Id == "PORTIA030");
        Assert.Contains(requirement, diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("OutputDiagnosticScenario.cs", diagnostic.Location.GetLineSpan().Path);
        Assert.DoesNotContain(diagnostics, d => d.Location.GetLineSpan().Path.EndsWith(".g.cs", StringComparison.Ordinal) && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void DiagnosesWrongContextBase()
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics(ContextUsings + """
            public sealed record Payload;
            [PortiaJsonContext, JsonSerializable(typeof(Payload))]
            public class Context { public Context(JsonSerializerOptions options) { } }
            """, new JsonRootMarkerGenerator());
        Assert.Single(diagnostics, d => d.Id == "PORTIA030" && d.GetMessage(CultureInfo.InvariantCulture).Contains("JsonSerializerContext", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, d => d.Id == "CS0029");
    }

    [Theory]
    [InlineData("Func<IEndpointRouteBuilder, string, IEndpointConventionBuilder> map = PortiaEndpointRouteBuilderExtensions.MapPortiaGet<Command>;")]
    [InlineData("Func<IEndpointRouteBuilder, string, IEndpointConventionBuilder> map = PortiaEndpointRouteBuilderExtensions.MapPortiaPost<Query, string>;")]
    [InlineData("Func<IEndpointRouteBuilder, string, IEndpointConventionBuilder> map = PortiaEndpointRouteBuilderExtensions.MapPortiaGetStream<Feed, string>;")]
    [InlineData("Func<IEndpointRouteBuilder, string, IEndpointConventionBuilder> map = PortiaEndpointRouteBuilderExtensions.MapPortiaGetSse<Feed, string>;")]
    [InlineData("Func<IEndpointRouteBuilder, string, Action<PortiaEndpointConfiguration<Command>>, IEndpointConventionBuilder> map = PortiaEndpointRouteBuilderExtensions.MapPortiaPost<Command>;")]
    [InlineData("Func<IEndpointRouteBuilder, string, Action<PortiaEndpointConfiguration<Query, string>>, IEndpointConventionBuilder> map = PortiaEndpointRouteBuilderExtensions.MapPortiaPost<Query, string>;")]
    [InlineData("Func<IEndpointRouteBuilder, string, Action<PortiaStreamingEndpointConfiguration<Feed>>, IEndpointConventionBuilder> map = PortiaEndpointRouteBuilderExtensions.MapPortiaGetSse<Feed, string>;")]
    [InlineData("System.Linq.Expressions.Expression<Func<IEndpointRouteBuilder, IEndpointConventionBuilder>> map = routes => routes.MapPortiaGet<Command>(\"/query\");")]
    public void DiagnosesUnsupportedHttpMappingForms(string statement)
    {
        var source = $$"""
            using System;
            using Cntryl.Portia;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using static Cntryl.Portia.PortiaEndpointRouteBuilderExtensions;
            public sealed record Command : IRequest, ICallable;
            public sealed record Query : IRequest<string>, ICallable;
            public sealed record Feed : IStreamRequest<string>, ICallable;
            public static class Endpoints { public static void Configure() { {{statement}} } }
            """;
        var diagnostics = GeneratorCompilation.OutputDiagnostics(source, new RequestHttpBindingGenerator());
        var diagnostic = Assert.Single(diagnostics, d => d.Id == "PORTIA016");
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Id != "PORTIA016");
        Assert.Contains("direct", diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StaticImportsKeepValidExtensionCallsAndCompilerDiagnosesBareMethodGroups()
    {
        const string prefix = """
            using System;
            using Cntryl.Portia;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Routing;
            using static Cntryl.Portia.PortiaEndpointRouteBuilderExtensions;
            public sealed record Command : IRequest, ICallable;
            """;
        var valid = GeneratorCompilation.OutputDiagnostics(prefix + """
            public static class Endpoints { public static void Configure(IEndpointRouteBuilder routes) =>
                routes.MapPortiaGet<Command>("/query"); }
            """, new RequestHttpBindingGenerator());
        Assert.DoesNotContain(valid, d => d.Severity == DiagnosticSeverity.Error);
        var invalid = GeneratorCompilation.OutputDiagnostics(prefix + """
            public static class Endpoints { public static void Configure() {
                Func<IEndpointRouteBuilder, string, IEndpointConventionBuilder> map = MapPortiaGet<Command>;
            } }
            """, new RequestHttpBindingGenerator());
        Assert.Single(invalid, d => d.Id == "CS0103");
    }

    [Fact]
    public void FactoriesComposeAcrossAssembliesWhoseNamesPreviouslySanitizedIdentically()
    {
        var source = ContextUsings + ManualContext("Context").Replace("typeof(Payload)", "typeof(string)", StringComparison.Ordinal);
        var first = GeneratorCompilation.ReferenceNamed("Contracts_A", source, new JsonRootMarkerGenerator());
        var second = GeneratorCompilation.ReferenceNamed("Contracts.A", source, new JsonRootMarkerGenerator());
        const string consumer = """
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            public static class App { public static void Configure(IServiceCollection services) => services.AddPortia(); }
            """;
        var diagnostics = GeneratorCompilation.OutputDiagnostics(consumer, [first, second], new RegistrationCallInterceptorGenerator());
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var output = GeneratorCompilation.GeneratedSource(consumer, [first, second], new RegistrationCallInterceptorGenerator());
        var control = GeneratorCompilation.GeneratedSource(consumer, new RegistrationCallInterceptorGenerator());
        Assert.Equal(2, output.Split(".Create(options)", StringSplitOptions.None).Length
            - control.Split(".Create(options)", StringSplitOptions.None).Length);
    }

    [Fact]
    public void ContextDeclarationOrderDoesNotChangeFactoryOutput()
    {
        var first = "namespace App_A {" + ManualContext("Context") + "}";
        var second = "namespace App {" + ManualContext("A_Context") + "}";
        var prefix = ContextUsings + "public sealed record Payload;";
        Assert.Equal(GeneratorCompilation.GeneratedSource(prefix + first + second, new JsonRootMarkerGenerator()),
            GeneratorCompilation.GeneratedSource(prefix + second + first, new JsonRootMarkerGenerator()));
    }

    [Fact]
    public void ReferencedLegacyFactoryMetadataRemainsAuthoritative()
    {
        var reference = GeneratorCompilation.Reference(ContextUsings + """
            [assembly: PortiaJsonRootAttribute(typeof(string), typeof(Context), typeof(LegacyFactory))]
            internal class Context : JsonSerializerContext
            {
                public Context(JsonSerializerOptions options) : base(options) { }
                protected override JsonSerializerOptions? GeneratedSerializerOptions => null;
                public override JsonTypeInfo? GetTypeInfo(Type type) => null;
            }
            public static class LegacyFactory
            {
                public static JsonSerializerContext Create(JsonSerializerOptions options) => new Context(options);
            }
            """);
        var consumer = """
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            public static class App { public static void Configure(IServiceCollection services) => services.AddPortia(); }
            """;
        var output = GeneratorCompilation.GeneratedSource(consumer, [reference], new RegistrationCallInterceptorGenerator());
        Assert.Contains("global::LegacyFactory.Create(options)", output, StringComparison.Ordinal);
        Assert.DoesNotContain(GeneratorCompilation.OutputDiagnostics(consumer, [reference], new RegistrationCallInterceptorGenerator()),
            d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void ObservationAnalyzerKeepsRetainedCallbacksExplicitExecutionAndSuppressions()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
            using System;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Cntryl.Portia.Testing;
            public sealed record Command : IRequest;
            public static class Tests
            {
                public static async Task Run(IServiceProvider services)
                {
                    var value = RequestScenario.For(services).When(new Command());
                    Func<RequestExpectations> callback = () => value.ExpectDenied();
                    RequestExpectations _;
                    _ = value.ExpectDenied();
                    await _.AsTask();
                    await callback();
            #pragma warning disable PORTIA107
                    _ = RequestScenario.For(services).When(new Command()).ExpectDenied();
            #pragma warning restore PORTIA107
                    Action executed = () => value.ExpectDenied().AsTask();
                    executed();
                }
            }
            """, new ScenarioObservationAnalyzer());
        Assert.DoesNotContain(diagnostics, d => d.Id == "PORTIA107");
    }

    [Fact]
    public void SuppressingContextDiagnosticCannotRegisterAnInvalidFactory()
    {
        var source = ContextUsings + """
            using Microsoft.Extensions.DependencyInjection;
            public sealed record Payload;
            """ + ManualContext("InvalidContext", missingConstructor: true) + """
            public static class App { public static void Configure(IServiceCollection services) => services.AddPortia(); }
            """;
        var generated = GeneratorCompilation.GeneratedSource(source,
            new JsonRootMarkerGenerator(), new RegistrationCallInterceptorGenerator());
        Assert.DoesNotContain("new global::InvalidContext", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("typeof(global::InvalidContext)", generated, StringComparison.Ordinal);
        _ = GeneratorCompilation.CompileSuppressing(source, "PORTIA030",
            new JsonRootMarkerGenerator(), new RegistrationCallInterceptorGenerator());
    }

    [Fact]
    public void EmptyPartialContextNeedsARootBeforeGeneratedRegistration()
    {
        var diagnostics = GeneratorCompilation.Diagnostics(ContextUsings + """
            [PortiaJsonContext] internal partial class EmptyContext : JsonSerializerContext;
            """, new JsonRootMarkerGenerator());
        Assert.Single(diagnostics, d => d.Id == "PORTIA030"
            && d.GetMessage(CultureInfo.InvariantCulture).Contains("JsonSerializable", StringComparison.Ordinal));
    }

    [Fact]
    public void ManualOptionsConstructorCanHaveOptionalArguments()
    {
        var source = ContextUsings + "public sealed record Payload;" +
            ManualContext("Context").Replace("Context(JsonSerializerOptions options)", "Context(JsonSerializerOptions options, bool unused = false)", StringComparison.Ordinal);
        Assert.DoesNotContain(GeneratorCompilation.OutputDiagnostics(source, new JsonRootMarkerGenerator()),
            d => d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("public required int Value { get; init; }", "required members")]
    [InlineData("public Context(JsonSerializerOptions options, bool other = false) : base(options) { } public Context(JsonSerializerOptions options, int other = 0) : base(options) { }", "ambiguous")]
    public void DiagnosesUnusableOptionsConstructorBeforeFactoryEmission(string member, string expected)
    {
        var context = ManualContext("Context");
        if (expected == "ambiguous")
            context = context.Replace("public Context(JsonSerializerOptions options) : base(options) { }", member, StringComparison.Ordinal);
        else
            context = context.Replace("protected override", member + " protected override", StringComparison.Ordinal);
        var diagnostics = GeneratorCompilation.OutputDiagnostics(ContextUsings + "public sealed record Payload;" + context,
            new JsonRootMarkerGenerator());
        Assert.Single(diagnostics, d => d.Id == "PORTIA030"
            && d.GetMessage(CultureInfo.InvariantCulture).Contains(expected, StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Location.GetLineSpan().Path.EndsWith(".g.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void HttpDocumentationReferencesDoNotRequireInterception()
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics("""
            using Cntryl.Portia;
            using Microsoft.AspNetCore.Routing;
            public sealed record Command : IRequest, ICallable;
            /// <summary>See <see cref="PortiaEndpointRouteBuilderExtensions.MapPortiaGet{Command}(IEndpointRouteBuilder, string)"/>.</summary>
            public static class Documentation { }
            """, new RequestHttpBindingGenerator());
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    const string ContextUsings = """
        using System;
        using System.Text.Json;
        using System.Text.Json.Serialization;
        using System.Text.Json.Serialization.Metadata;
        using Cntryl.Portia;
        """;

    static string ManualContext(string name, string visibility = "internal", string constructorVisibility = "public", bool missingConstructor = false)
    {
        var simple = name.Split('<')[0];
        var constructor = missingConstructor ? $"public {simple}() : base(null) {{ }}" :
            $"{constructorVisibility} {simple}(JsonSerializerOptions options) : base(options) {{ }}";
        return $$"""
            [PortiaJsonContext, JsonSerializable(typeof(Payload))]
            {{visibility}} class {{name}} : JsonSerializerContext
            {
                {{constructor}}
                protected override JsonSerializerOptions? GeneratedSerializerOptions => null;
                public override JsonTypeInfo? GetTypeInfo(Type type) => null;
            }
            """;
    }
}
