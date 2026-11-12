using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

/// <summary>
///     Covers what the registration interceptor emits after an incremental rerun. Roslyn caches a
///     syntax-provider transform per node, so anything the transform reads beyond its own node — the
///     compilation's events and JSON contexts among them — must reach the output through its own
///     pipeline stage, or an edit in another file is never reflected in an unchanged call site's
///     generated registration.
/// </summary>
public sealed class RegistrationInterceptorIncrementalTests
{
    const string CallSite = """
                            using Cntryl.Portia;
                            using Microsoft.Extensions.DependencyInjection;

                            public static class Composition
                            {
                                public static PortiaBuilder Compose(IServiceCollection services) => services.AddPortia();
                            }
                            """;

    const string FirstEvent = """
                              using Cntryl.Portia;

                              [Discriminator("incremental.first", 1)]
                              public sealed record IncrementalFirst : DomainEvent;
                              """;

    const string SecondEvent = """
                               using Cntryl.Portia;

                               [Discriminator("incremental.second", 1)]
                               public sealed record IncrementalSecond : DomainEvent;
                               """;

    /// <summary>
    ///     Verifies an event added in a new file after a first generator run reaches the unchanged
    ///     <c>AddPortia()</c> call site's generated registration, so its JSON root and catalog entry
    ///     are not silently missing for the rest of the editing session.
    /// </summary>
    [Fact]
    public void ShouldRegisterEventGivenEventAddedAfterIncrementalRerun()
    {
        // Arrange
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview).WithFeatures(
            [new KeyValuePair<string, string>("InterceptorsNamespaces", "Cntryl.Portia.Generated")]);
        var compilation = CSharpCompilation.Create("RegistrationIncremental",
            [
                CSharpSyntaxTree.ParseText(CallSite, parseOptions, "Composition.cs"),
                CSharpSyntaxTree.ParseText(FirstEvent, parseOptions, "IncrementalFirst.cs")
            ],
            References(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new RegistrationCallInterceptorGenerator().AsSourceGenerator()], parseOptions: parseOptions);

        // Act
        driver = driver.RunGenerators(compilation);

        // Assert
        Assert.Contains("IncrementalFirst", Generated(driver), StringComparison.Ordinal);

        driver = driver.RunGenerators(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(SecondEvent, parseOptions, "IncrementalSecond.cs")));

        Assert.Contains("IncrementalSecond", Generated(driver), StringComparison.Ordinal);
    }

    /// <summary>
    ///     Verifies a registration call site's cached model does not depend on the rest of the
    ///     compilation. Reading every event and JSON context from inside the per-call-site transform
    ///     makes each one re-derive the whole compilation on every edit and invalidates every call
    ///     site whenever any event changes; those inputs belong in their own pipeline stage.
    /// </summary>
    [Fact]
    public void ShouldNotInvalidateCallSiteModelsGivenUnrelatedEventAdded()
    {
        // Arrange
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview).WithFeatures(
            [new KeyValuePair<string, string>("InterceptorsNamespaces", "Cntryl.Portia.Generated")]);
        var compilation = CSharpCompilation.Create("RegistrationIncrementalCache",
            [
                CSharpSyntaxTree.ParseText(CallSite, parseOptions, "Composition.cs"),
                CSharpSyntaxTree.ParseText(FirstEvent, parseOptions, "IncrementalFirst.cs")
            ],
            References(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new RegistrationCallInterceptorGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(default, true));

        driver = driver.RunGenerators(compilation);
        driver = driver.RunGenerators(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText(SecondEvent, parseOptions, "IncrementalSecond.cs")));

        // Act
        var reasons = driver.GetRunResult().Results.Single()
            .TrackedSteps["PortiaRegistrationCalls"]
            .SelectMany(static step => step.Outputs)
            .Select(static output => output.Reason)
            .ToArray();

        // Assert
        Assert.NotEmpty(reasons);
        Assert.All(reasons, reason => Assert.True(
            reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
            $"Call-site models were {reason} after an unrelated event was added."));
    }

    [Fact]
    public void ShouldRefreshGeneratedMcpDescriptionGivenDocumentationOnlyEdit()
    {
        // Arrange
        const string initialRequest = """
            using Cntryl.Portia;
            /// <summary>Finds an account.</summary>
            /// <param name="AccountId">The stable account key.</param>
            [Discriminator("account.find", 1)]
            public sealed record FindAccount(string AccountId) : IRequest<string>, ICallable;
            """;
        const string editedRequest = """
            using Cntryl.Portia;
            /// <summary>Finds an account by its public identifier.</summary>
            /// <param name="AccountId">The public account key.</param>
            [Discriminator("account.find", 1)]
            public sealed record FindAccount(string AccountId) : IRequest<string>, ICallable;
            """;
        const string composition = """
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            public static class Composition
            {
                public static PortiaBuilder Compose(IServiceCollection services) => services.AddPortia().AddMcpTool<FindAccount>();
            }
            """;
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview).WithFeatures(
            [new KeyValuePair<string, string>("InterceptorsNamespaces", "Cntryl.Portia.Generated")]);
        var requestTree = CSharpSyntaxTree.ParseText(initialRequest, parseOptions, "FindAccount.cs");
        var callSiteTree = CSharpSyntaxTree.ParseText(composition, parseOptions, "Composition.cs");
        var compilation = CSharpCompilation.Create("McpDescriptionIncremental", [callSiteTree, requestTree],
            References().Append(MetadataReference.CreateFromFile(typeof(McpToolOptions).Assembly.Location)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new RegistrationCallInterceptorGenerator().AsSourceGenerator()], parseOptions: parseOptions);

        // Act
        driver = driver.RunGenerators(compilation);

        // Assert
        Assert.Contains("Finds an account.", Generated(driver), StringComparison.Ordinal);
        Assert.Contains("The stable account key.", Generated(driver), StringComparison.Ordinal);

        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(requestTree,
            CSharpSyntaxTree.ParseText(editedRequest, parseOptions, "FindAccount.cs")));

        Assert.Contains("Finds an account by its public identifier.", Generated(driver), StringComparison.Ordinal);
        Assert.Contains("The public account key.", Generated(driver), StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldReadMcpDescriptionsGivenReferencedAssemblyXmlFile()
    {
        // Arrange
        const string source = """
            using Cntryl.Portia;
            using Cntryl.Portia.McpContracts;
            using Microsoft.Extensions.DependencyInjection;
            public static class Composition
            {
                public static PortiaBuilder Compose(IServiceCollection services) =>
                    services.AddPortia().AddMcpTool<ExternalGreeting>();
            }
            """;
        var contractsAssembly = typeof(Cntryl.Portia.McpContracts.ExternalGreeting).Assembly.Location;

        // Act
        var documentationFile = Path.ChangeExtension(contractsAssembly, ".xml");

        // Assert
        Assert.True(File.Exists(documentationFile), $"Expected XML documentation at {documentationFile}.");
        var references = new MetadataReference[]
        {
            MetadataReference.CreateFromFile(typeof(McpToolOptions).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(IRequestBase).Assembly.Location),
            MetadataReference.CreateFromFile(contractsAssembly, documentation: XmlDocumentationProvider.CreateFromFile(documentationFile))
        };

        var generated = GeneratorCompilation.GeneratedSource(source, references,
            new RegistrationCallInterceptorGenerator());

        Assert.Contains("Reads a greeting defined in a separate contracts assembly.", generated,
            StringComparison.Ordinal);
        Assert.Contains("The person's name from the separate contracts assembly.", generated,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldGenerateMcpPropertyDescriptionsDeterministicallyGivenSameInput()
    {
        // Arrange
        const string declarationOrder = """
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            /// <summary>Looks up a stable code.</summary>
            /// <param name="Zulu">The high range code.</param>
            /// <param name="Beta">The low range code.</param>
            [Discriminator("stable.lookup", 1)]
            public sealed record StableLookup(string Zulu, string Beta) : IRequest<string>, ICallable;
            public static class Composition
            {
                public static PortiaBuilder Compose(IServiceCollection services) => services.AddPortia().AddMcpTool<StableLookup>();
            }
            """;
        const string reversedDeclarationOrder = """
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            /// <summary>Looks up a stable code.</summary>
            /// <param name="Zulu">The high range code.</param>
            /// <param name="Beta">The low range code.</param>
            [Discriminator("stable.lookup", 1)]
            public sealed record StableLookup(string Beta, string Zulu) : IRequest<string>, ICallable;
            public static class Composition
            {
                public static PortiaBuilder Compose(IServiceCollection services) => services.AddPortia().AddMcpTool<StableLookup>();
            }
            """;

        var first = GeneratorCompilation.GeneratedSource(declarationOrder, new RegistrationCallInterceptorGenerator());
        var second = GeneratorCompilation.GeneratedSource(reversedDeclarationOrder, new RegistrationCallInterceptorGenerator());

        var firstDescriptions = string.Join(Environment.NewLine, first.Split(Environment.NewLine)
            .Where(line => line.Contains("DescribeProperty(", StringComparison.Ordinal)));

        // Act
        var secondDescriptions = string.Join(Environment.NewLine, second.Split(Environment.NewLine)
            .Where(line => line.Contains("DescribeProperty(", StringComparison.Ordinal)));

        // Assert
        Assert.Equal(firstDescriptions, secondDescriptions);
        Assert.True(firstDescriptions.IndexOf("DescribeProperty(\"Beta\"", StringComparison.Ordinal)
                    < firstDescriptions.IndexOf("DescribeProperty(\"Zulu\"", StringComparison.Ordinal));
    }

    /// <summary>A request sent only through the outcome-aware API still enters the generated transport catalog.</summary>
    [Fact]
    public void ShouldRegisterRequestGivenDispatchThroughOutcomeAwareSender()
    {
        // Arrange
        const string source = """
                                using Cntryl.Portia;
                                using Microsoft.Extensions.DependencyInjection;
                                [RequestRoute("test", "generators", "outcome", "only")]
                                [Discriminator("outcome.only", 1)]
                                public sealed record OutcomeOnlyRequest : IRequest<int>, ICallable;
                                public static class Composition
                                {
                                    public static PortiaBuilder Compose(IServiceCollection services) => services.AddPortia();
                                    public static void Dispatch(IRemoteRequestSender sender) => _ =
                                        sender.SendWithOutcomeAsync<OutcomeOnlyRequest, int>(new OutcomeOnlyRequest(),
                                            new RequestRouteValues(), null);
                                }
                                """;
        var assembly = GeneratorCompilation.Compile(source, new RegistrationCallInterceptorGenerator());
        var services = new ServiceCollection();

        // Act
        _ = assembly.GetType("Composition")!.GetMethod("Compose")!.Invoke(null, [services]);

        // Assert
        var registration = Assert.Single(services.Where(descriptor =>
                descriptor.ServiceType == typeof(RequestTransportRegistration))
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<RequestTransportRegistration>());

        Assert.Equal(assembly.GetType("OutcomeOnlyRequest"), registration.RequestType);
        Assert.Equal("outcome.only", registration.Discriminator.Name);
    }

    static string Generated(GeneratorDriver driver)
    {
        var result = driver.GetRunResult();
        Assert.All(result.Results, item => Assert.Null(item.Exception));
        return string.Join(Environment.NewLine, result.GeneratedTrees.Select(tree => tree.GetText().ToString()));
    }

    [Theory]
    [InlineData("Imported.Changed", "new Alias()")]
    [InlineData("object", "new Imported.Changed()")]
    [InlineData("Imported", "new Alias.Changed()")]
    public void ShouldDiscoverReferencedEventsGivenTypeOrNamespaceAliases(string aliasTarget, string creation)
    {
        // Arrange
        var reference = GeneratorCompilation.ReferenceNamed("AliasFeature", """
            using Cntryl.Portia;
            namespace Imported;
            [Discriminator("alias.changed", 1)] public sealed record Changed : DomainEvent;
            """);
        var source = $$"""
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            using Alias = {{aliasTarget}};
            public static class Composition
            {
                public static PortiaBuilder Compose(IServiceCollection services) => services.AddPortia();
                public static DomainEvent Fact() => {{creation}};
            }
            """;

        // Act
        var generated = GeneratorCompilation.GeneratedSource(source, [reference], new RegistrationCallInterceptorGenerator());
        var diagnostics = GeneratorCompilation.OutputDiagnostics(source, [reference], new RegistrationCallInterceptorGenerator());

        // Assert
        Assert.Contains("AddGeneratedEvent<global::Imported.Changed>", generated, StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    }

    static IEnumerable<MetadataReference> References() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Append(typeof(Aggregate).Assembly.Location)
        .Append(typeof(IRemoteRequestSender).Assembly.Location)
        .Append(typeof(PortiaBuilder).Assembly.Location)
        .Distinct(StringComparer.Ordinal)
        .Select(path => MetadataReference.CreateFromFile(path));
}
