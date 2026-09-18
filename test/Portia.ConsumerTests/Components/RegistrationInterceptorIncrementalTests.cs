using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

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
    public void ShouldRegisterAnEventAddedAfterAnIncrementalRerun()
    {
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

        driver = driver.RunGenerators(compilation);
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
    public void ShouldNotInvalidateCallSiteModelsWhenAnUnrelatedEventIsAdded()
    {
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

        var reasons = driver.GetRunResult().Results.Single()
            .TrackedSteps["PortiaRegistrationCalls"]
            .SelectMany(static step => step.Outputs)
            .Select(static output => output.Reason)
            .ToArray();

        Assert.NotEmpty(reasons);
        Assert.All(reasons, reason => Assert.True(
            reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
            $"Call-site models were {reason} after an unrelated event was added."));
    }

    static string Generated(GeneratorDriver driver)
    {
        var result = driver.GetRunResult();
        Assert.All(result.Results, item => Assert.Null(item.Exception));
        return string.Join(Environment.NewLine, result.GeneratedTrees.Select(tree => tree.GetText().ToString()));
    }

    static IEnumerable<MetadataReference> References() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Append(typeof(Aggregate).Assembly.Location)
        .Append(typeof(PortiaBuilder).Assembly.Location)
        .Distinct(StringComparer.Ordinal)
        .Select(path => MetadataReference.CreateFromFile(path));
}
