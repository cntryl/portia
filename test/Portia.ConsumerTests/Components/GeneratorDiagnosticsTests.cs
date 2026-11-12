using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cntryl.Portia.Consumer;

public sealed class GeneratorDiagnosticsTests
{
    [Fact]
    public void ShouldRecognizePortia100GivenKnownEffectDependenciesThroughTypeAncestry()
    {
        // Arrange
        const string source = """
                              using Cntryl.Portia;
                              using System.Data.Common;
                              using System.Net.Http;
                              using System.Net.Mail;
                              namespace Microsoft.EntityFrameworkCore { public class DbContext {} }
                              namespace Grpc.Core { public abstract class ClientBase<T> {} }
                              namespace Stripe { public interface IStripeClient {} }
                              public sealed class AppDb : Microsoft.EntityFrameworkCore.DbContext;
                              public abstract class AppConnection : DbConnection;
                              public sealed class RpcClient : Grpc.Core.ClientBase<RpcClient>;
                              public sealed class Projection(
                                  IRequestBus bus, HttpClient http, IHttpClientFactory factory, SmtpClient smtp,
                                  Stripe.IStripeClient stripe, AppDb db, AppConnection connection, RpcClient rpc)
                                  : Projector(null!, EventStreamPattern.ForPattern("events"));
                              """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer())
            .Where(diagnostic => diagnostic.Id == "PORTIA100").ToArray();

        // Assert
        Assert.Equal(8, diagnostics.Length);
        Assert.All(diagnostics, diagnostic =>
        {
            Assert.StartsWith("Projector 'Projection' takes known effect dependency '",
                diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            var location = source.Substring(diagnostic.Location.SourceSpan.Start,
                diagnostic.Location.SourceSpan.Length);
            Assert.True(location is "bus" or "http" or "factory" or "smtp" or "stripe" or "db" or "connection"
                    or "rpc", $"Unexpected diagnostic location '{location}'.");
        });
    }

    [Fact]
    public void ShouldReportPortia100GivenProjectorDependingOnAggregateWriterAndExecutorButNotReader()
    {
        // Arrange
        const string source = """
                              using Cntryl.Portia;
                              public sealed class Projection(IAggregateWriter writer, IAggregateExecutor executor, IAggregateReader reader)
                                  : Projector(null!, EventStreamPattern.ForPattern("events"));
                              """;

        // Act
        var locations = Locations(source, GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer())
            .Where(diagnostic => diagnostic.Id == "PORTIA100"));

        // Assert
        Assert.Equal(["executor", "writer"], locations);
    }

    [Fact]
    public void ShouldReportPortia102GivenAggregateReaderWriterAndExecutorDependencies()
    {
        // Arrange
        const string source = """
                              using Cntryl.Portia;
                              public sealed class Account(IAggregateReader reader, IAggregateWriter writer, IAggregateExecutor executor)
                                  : Aggregate(Uuid.CreateVersion4(), new EventStreamAddress("bank", "accounts", "one"));
                              """;

        // Act
        var locations = Locations(source, GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer())
            .Where(diagnostic => diagnostic.Id == "PORTIA102"));

        // Assert
        Assert.Equal(["executor", "reader", "writer"], locations);
    }

    [Theory]
    [InlineData("PORTIA105", "IRequestGuard<Command>",
        "public ValueTask<Result> GuardAsync(IRequestContext<Command> context, CancellationToken ct) => default;",
        "Request guard 'Component' takes effect-capable dependency '")]
    [InlineData("PORTIA106", "IRequestAuthorizer<Command>",
        "public ValueTask<Result> AuthorizeAsync(IRequestContext<Command> context, CancellationToken ct) => default;",
        "Request authorizer 'Component' takes effect-capable dependency '")]
    public void ShouldReportPortia105And106GivenPreflightEffectCapableDependencies(string id, string role, string member,
        string messagePrefix)
    {
        // Arrange
        var source = $$"""
                       using System.Net.Http;
                       using System.Threading;
                       using System.Threading.Tasks;
                       using Cntryl.Portia;
                       public sealed record Command : IRequest;
                       public sealed class Component(
                           IRequestBus bus, IAggregateWriter writer, IAggregateExecutor executor,
                           IEventStore store, IDomainEventWriter events, IProjectionStore projections,
                           IProjectionCheckpointStore checkpoints, IRequestScheduler scheduler, HttpClient http) : {{role}}
                       {
                           {{member}}
                       }
                       """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer())
            .Where(diagnostic => diagnostic.Id == id).ToArray();

        // Assert
        Assert.Equal(
            ["bus", "checkpoints", "events", "executor", "projections", "scheduler", "store", "writer"],
            Locations(source, diagnostics));
        Assert.All(diagnostics, diagnostic => Assert.StartsWith(messagePrefix,
            diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        Assert.Contains("IAggregateReader", MessageAt(source, diagnostics, "writer"), StringComparison.Ordinal);
        Assert.Contains("IDomainEventReader", MessageAt(source, diagnostics, "store"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("IRequestGuard<Command>",
        "public ValueTask<Result> GuardAsync(IRequestContext<Command> context, CancellationToken ct) => default;")]
    [InlineData("IRequestAuthorizer<Command>",
        "public ValueTask<Result> AuthorizeAsync(IRequestContext<Command> context, CancellationToken ct) => default;")]
    public void ShouldAllowPortia105And106GivenReadOnlyDependencies(string role, string member)
    {
        // Arrange
        var source = $$"""
                       using System.Threading;
                       using System.Threading.Tasks;
                       using Cntryl.Portia;
                       public interface ITenantDirectory;
                       public sealed record Command : IRequest;
                       public sealed class Component(IAggregateReader aggregates, IDomainEventReader events, ITenantDirectory tenants) : {{role}}
                       {
                           {{member}}
                       }
                       """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "PORTIA105" or "PORTIA106");
    }

    [Fact]
    public void ShouldIgnorePortia105And106GivenTypesThatAreNotGuardsOrAuthorizers()
    {
        // Arrange
        const string source = """
                                                           using Cntryl.Portia;
                                                           public sealed class ApplicationService(IRequestBus bus, IAggregateWriter writer);
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "PORTIA105" or "PORTIA106");
    }

    static string[] Locations(string source, IEnumerable<Diagnostic> diagnostics) =>
    [
        .. diagnostics.Select(diagnostic => source.Substring(diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length)).Order(StringComparer.Ordinal)
    ];

    static string MessageAt(string source, IEnumerable<Diagnostic> diagnostics, string parameter) =>
        diagnostics.Single(diagnostic => source.Substring(diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length) == parameter).GetMessage(CultureInfo.InvariantCulture);

    [Fact]
    public void ShouldReportPortia101GivenConstructorAndActivatorUtilitiesServiceLocationAtUse()
    {
        // Arrange
        const string source = """
                              using Cntryl.Portia;
                              using Microsoft.Extensions.DependencyInjection;
                              using System;
                              public sealed class Projection(IServiceProvider provider, IServiceScopeFactory scopes,
                                  IServiceScope scope) : Projector(null!, EventStreamPattern.ForPattern("events"))
                              {
                                  public object Locate() => ActivatorUtilities.CreateInstance<object>(provider);
                              }
                              """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer())
            .Where(diagnostic => diagnostic.Id == "PORTIA101").ToArray();

        // Assert
        Assert.Equal(4, diagnostics.Length);
        var invocation = Assert.Single(diagnostics, diagnostic => diagnostic
            .GetMessage(CultureInfo.InvariantCulture)
            .Contains("ActivatorUtilities.CreateInstance", StringComparison.Ordinal));
        Assert.Equal("ActivatorUtilities.CreateInstance<object>(provider)",
            source.Substring(invocation.Location.SourceSpan.Start, invocation.Location.SourceSpan.Length));
        Assert.All(diagnostics, diagnostic => Assert.StartsWith("'Projection' uses service location through '",
            diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    [Fact]
    public void ShouldIgnoreLookalikesGivenUnrecognizedGatewaysInPracticeDiagnostics()
    {
        // Arrange
        const string source = """
                                                           using Cntryl.Portia;
                                                           public sealed class ApplicationGateway;
                                                           public static class ActivatorUtilities
                                                           {
                                                               public static object CreateInstance() => new();
                                                           }
                                                           public sealed class Projection(ApplicationGateway gateway)
                                                               : Projector(null!, EventStreamPattern.ForPattern("events"))
                                                           {
                                                               public object Call() => ActivatorUtilities.CreateInstance();
                                                           }
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "PORTIA100" or "PORTIA101");
    }

    [Fact]
    public void ShouldNameProcessorAndHandlerGivenBatchHandlerWithoutBatchBase()
    {
        // Arrange
        const string source = """
                              using Cntryl.Portia;
                              public sealed record Changed : DomainEvent;
                              public sealed partial class Projection
                                  : Projector(null!, EventStreamPattern.ForPattern("events")), IBatchProjectorHandler<Changed>;
                              """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source,
            new ProjectorReactorEventDispatcherGenerator());

        // Assert
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA017");
        Assert.Equal("Processor 'Projection' must use a batch base to implement handler " +
                     "'Cntryl.Portia.IBatchProjectorHandler<Changed>'",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.Equal("Projection", source.Substring(diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length));
    }

    [Fact]
    public void ShouldNameProcessorAndEventGivenSingleAndBatchHandlerForOneEvent()
    {
        // Arrange
        const string source = """
                              using Cntryl.Portia;
                              public sealed record Changed : DomainEvent;
                              public sealed partial class Projection
                                  : BatchProjector(null!, EventStreamPattern.ForPattern("events")),
                                    IProjectorHandler<Changed>, IBatchProjectorHandler<Changed>;
                              """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source,
            new ProjectorReactorEventDispatcherGenerator());

        // Assert
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA028");
        Assert.Equal("Processor 'Projection' selects both single and batch handling for event 'Changed'",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.Equal("Projection", source.Substring(diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length));
    }

    [Fact]
    public void ShouldNameBothClrTypesGivenDuplicateDomainEvent()
    {
        // Arrange
        const string source = """
                              using Cntryl.Portia;
                              [Discriminator("changed", 2)]
                              public sealed record Original : DomainEvent;
                              [Discriminator("changed", 2)]
                              public sealed record Duplicate : DomainEvent;
                              """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source,
            new DomainEventCatalogGenerator());

        // Assert
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA023");
        Assert.Equal("Domain-event CLR types 'Original' and 'Duplicate' both declare discriminator 'changed' " +
                     "version 2", diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.Equal("Duplicate", source.Substring(diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length));
    }

    [Fact]
    public void ShouldNotReportDuplicateDiscriminatorGivenInferredExternalDomainEventLocals()
    {
        // Arrange
        var contracts = GeneratorCompilation.Reference("""
                                                       using Cntryl.Portia;
                                                       namespace Contracts;
                                                       [Discriminator("boundary.draft.created", 1)]
                                                       public sealed record BoundaryDraftCreated : DomainEvent;
                                                       [Discriminator("boundary.draft.revised", 1)]
                                                       public sealed record BoundaryDraftRevised : DomainEvent;
                                                       """);
        const string source = """
                              #nullable enable
                              using Contracts;
                              public static class Scenario
                              {
                                  public static void Run()
                                  {
                                      var created = new BoundaryDraftCreated();
                                      var revised = new BoundaryDraftRevised();
                                  }
                              }
                              """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, [contracts],
            new DomainEventCatalogGenerator());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA023");
    }

    [Fact]
    public void ShouldNotReportDuplicateDiscriminatorGivenExplicitBaseTypedExternalDomainEventLocals()
    {
        // Arrange
        var contracts = GeneratorCompilation.Reference("""
                                                       using Cntryl.Portia;
                                                       namespace Contracts;
                                                       [Discriminator("boundary.draft.created", 1)]
                                                       public sealed record BoundaryDraftCreated : DomainEvent;
                                                       [Discriminator("boundary.draft.revised", 1)]
                                                       public sealed record BoundaryDraftRevised : DomainEvent;
                                                       """);
        const string source = """
                              using Cntryl.Portia;
                              using Contracts;
                              public static class Scenario
                              {
                                  public static void Run()
                                  {
                                      DomainEvent created = new BoundaryDraftCreated();
                                      DomainEvent revised = new BoundaryDraftRevised();
                                  }
                              }
                              """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, [contracts],
            new DomainEventCatalogGenerator());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA023");
    }

    [Fact]
    public void ShouldReportDuplicateDiscriminatorGivenDistinctExternalDomainEventTypes()
    {
        // Arrange
        var contracts = GeneratorCompilation.Reference("""
                                                       using Cntryl.Portia;
                                                       namespace Contracts;
                                                       [Discriminator("boundary.draft.changed", 1)]
                                                       public sealed record BoundaryDraftCreated : DomainEvent;
                                                       [Discriminator("boundary.draft.changed", 1)]
                                                       public sealed record BoundaryDraftRevised : DomainEvent;
                                                       """);

        const string source = """
                              #nullable enable
                              using Contracts;
                              public static class Scenario
                              {
                                  public static void Run()
                                  {
                                      var created = new BoundaryDraftCreated();
                                      var revised = new BoundaryDraftRevised();
                                  }
                              }
                              """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, [contracts],
            new DomainEventCatalogGenerator());

        // Assert
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA023");
        Assert.Equal("Domain-event CLR types 'Contracts.BoundaryDraftCreated' and " +
                     "'Contracts.BoundaryDraftRevised' both declare discriminator 'boundary.draft.changed' version 1",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ShouldNameBothClrTypesGivenDuplicateRequest()
    {
        // Arrange
        const string source = """
                              using Cntryl.Portia;
                              [RequestRoute("*", "orders", "order", "create")]
                              [Discriminator("orders.create", 2)]
                              public sealed record Original : IRequest, IQueuable;
                              [RequestRoute("*", "orders", "order", "duplicate")]
                              [Discriminator("orders.create", 2)]
                              public sealed record Duplicate : IRequest, IQueuable;
                              """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source,
            new PortiaServiceRegistrationGenerator());

        // Assert
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA022");
        Assert.Equal("Request CLR types 'Original' and 'Duplicate' both declare discriminator 'orders.create' " +
                     "version 2", diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.Equal("Duplicate", source.Substring(diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length));
    }

    [Fact]
    public void ShouldMoveDuplicateRequestDiagnosticGivenMovedDeclarationWhenIncrementalRerun()
    {
        // Arrange
        const string source = """
                              using Cntryl.Portia;
                              [RequestRoute("*", "orders", "order", "create")]
                              [Discriminator("orders.create", 2)]
                              public sealed record Original : IRequest, IQueuable;
                              [RequestRoute("*", "orders", "order", "duplicate")]
                              [Discriminator("orders.create", 2)]
                              public sealed record Duplicate : IRequest, IQueuable;
                              """;
        var parseOptions = new CSharpParseOptions(
            LanguageVersion.Preview);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Aggregate).Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions, "Requests.cs");
        var compilation = CSharpCompilation.Create("DiagnosticRelocation", [tree],
            references, new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new PortiaServiceRegistrationGenerator().AsSourceGenerator()], parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);

        var movedSource = Environment.NewLine + source;
        var movedTree = CSharpSyntaxTree.ParseText(movedSource, parseOptions,
            "Requests.cs");
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(tree, movedTree));

        var result = driver.GetRunResult().Results.Single();

        // Act
        var semanticOutputs = result.TrackedSteps["PortiaRequestTransports"].SelectMany(step => step.Outputs).ToArray();

        // Assert
        Assert.NotEmpty(semanticOutputs);
        Assert.All(semanticOutputs, output => Assert.Equal(IncrementalStepRunReason.Cached, output.Reason));
        var diagnosticOutputs = result.TrackedSteps["PortiaRequestTransportLocations"]
            .SelectMany(step => step.Outputs).ToArray();
        Assert.Contains(diagnosticOutputs, output => output.Reason == IncrementalStepRunReason.Modified);

        var diagnostic = Assert.Single(result.Diagnostics, item => item.Id == "PORTIA022");
        Assert.Equal("Duplicate", movedSource.Substring(diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length));
        Assert.Equal(7, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public void ShouldReportPortia015GivenGenericHandler()
    {
        // Arrange
        const string source = """
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           using System.Threading;
                                                           using System.Threading.Tasks;
                                                           public sealed record Request : IRequest;
                                                           public class Handler<T> : IRequestHandler<Request>
                                                           {
                                                               public ValueTask<Result> HandleAsync(IRequestContext<Request> c, CancellationToken ct) => ValueTask.FromResult(Result.Success);
                                                           }
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new RequestShapeDiagnosticsGenerator());

        // Assert
        var diagnostic = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "PORTIA015");
        Assert.Equal("Component 'Handler<T>' cannot be generated: generic component types are unsupported; " +
                     "use a closed, non-generic component class",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     PORTIA015 and PORTIA025 are build-soundness errors, so the generators report them: a build that skips
    ///     analyzers still fails.
    /// </summary>
    [Fact]
    public void ShouldReportBuildSoundnessErrorsGivenGeneratorsAlone()
    {
        // Arrange
        const string source = """
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Cntryl.Portia;
                              using Microsoft.AspNetCore.Routing;
                              public sealed record Query : IRequest, ICallable;
                              public class Handler<T> : IRequestHandler<Query>
                              {
                                  public ValueTask<Result> HandleAsync(IRequestContext<Query> c, CancellationToken ct) => ValueTask.FromResult(Result.Success);
                              }
                              public static class Endpoints
                              {
                                  public static void Map(IEndpointRouteBuilder routes) => routes.MapPortiaPost<Query>("/query");
                              }
                              """;
        IIncrementalGenerator[] generators =
        [
            .. new[] { typeof(DomainEventCatalogGenerator).Assembly, typeof(RequestHttpBindingGenerator).Assembly }
                .SelectMany(assembly => assembly.GetTypes())
                .Where(type => typeof(IIncrementalGenerator).IsAssignableFrom(type) && !type.IsAbstract)
                .Select(type => (IIncrementalGenerator)Activator.CreateInstance(type)!)
        ];

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, generators);

        // Assert
        _ = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "PORTIA015");
        // Reported where the application maps the endpoint, never inside Portia's generated interceptor.
        var missing = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "PORTIA025");
        Assert.Equal("DiagnosticScenario.cs", missing.Location.GetLineSpan().Path);
        Assert.Equal("routes.MapPortiaPost<Query>(\"/query\")",
            source.Substring(missing.Location.SourceSpan.Start, missing.Location.SourceSpan.Length));
    }

    [Fact]
    public void ShouldReportPortia020GivenTransportedRequestWithoutDiscriminator()
    {
        // Arrange
        const string source = """
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           [RequestRoute("*", "orders", "order", "create")]
                                                           public sealed record CreateOrder : IRequest, IQueuable;
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new PortiaServiceRegistrationGenerator());

        // Assert
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA020");
    }

    [Theory]
    [InlineData("PORTIA020",
        "[RequestRoute(\"*\", \"orders\", \"order\", \"create\")]\npublic sealed record CreateOrder : IRequest, IQueuable;")]
    [InlineData("PORTIA024",
        "[RequestRoute(\"*\", \"bad/area\", \"order\", \"create\")]\n[Discriminator(\"orders.create\")]\npublic sealed record CreateOrder : IRequest, IQueuable;")]
    [InlineData("PORTIA029",
        "[RequestRoute(\"*\", \"orders\", \"order\", \"create\")]\n[Discriminator(\"orders.create\")]\npublic sealed record CreateOrder : IRequest, IBadTransport;\n[RequestTransport(\"bad/id\")] public interface IBadTransport;")]
    public void ShouldHonorPragmaSuppressionGivenPragmaWithoutRetainingSyntaxTrees(string id, string declaration)
    {
        // Arrange
        var source = $$"""
                                                             using Cntryl.Portia;
                                                             #pragma warning disable {{id}}
                                                             {{declaration}}
                                                             #pragma warning restore {{id}}
                                                             """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new PortiaServiceRegistrationGenerator());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == id);
    }

    [Theory]
    [InlineData("PORTIA020",
        "[RequestRoute(\"*\", \"orders\", \"order\", \"create\")]\npublic sealed record CreateOrder : IRequest, IQueuable;")]
    [InlineData("PORTIA024",
        "[RequestRoute(\"*\", \"bad/area\", \"order\", \"create\")]\n[Discriminator(\"orders.create\")]\npublic sealed record CreateOrder : IRequest, IQueuable;")]
    [InlineData("PORTIA029",
        "[RequestRoute(\"*\", \"orders\", \"order\", \"create\")]\n[Discriminator(\"orders.create\")]\npublic sealed record CreateOrder : IRequest, IBadTransport;\n[RequestTransport(\"bad/id\")] public interface IBadTransport;")]
    public void ShouldIgnorePragmaGivenInactivePragmaDisable(string id, string declaration)
    {
        // Arrange
        var source = $$"""
                                                             using Cntryl.Portia;
                                                             #if false
                                                             #pragma warning disable {{id}}
                                                             #endif
                                                             {{declaration}}
                                                             """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new PortiaServiceRegistrationGenerator());

        // Assert
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == id);
    }

    [Theory]
    [InlineData("PORTIA020",
        "[RequestRoute(\"*\", \"orders\", \"order\", \"create\")]\npublic sealed record CreateOrder : IRequest, IQueuable;")]
    [InlineData("PORTIA024",
        "[RequestRoute(\"*\", \"bad/area\", \"order\", \"create\")]\n[Discriminator(\"orders.create\")]\npublic sealed record CreateOrder : IRequest, IQueuable;")]
    [InlineData("PORTIA029",
        "[RequestRoute(\"*\", \"orders\", \"order\", \"create\")]\n[Discriminator(\"orders.create\")]\npublic sealed record CreateOrder : IRequest, IBadTransport;\n[RequestTransport(\"bad/id\")] public interface IBadTransport;")]
    public void ShouldIgnorePragmaGivenInactivePragmaRestore(string id, string declaration)
    {
        // Arrange
        var source = $$"""
                                                             using Cntryl.Portia;
                                                             #pragma warning disable {{id}}
                                                             #if false
                                                             #pragma warning restore {{id}}
                                                             #endif
                                                             {{declaration}}
                                                             """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new PortiaServiceRegistrationGenerator());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == id);
    }

    [Theory]
    [InlineData("PORTIA020",
        "[RequestRoute(\"*\", \"orders\", \"order\", \"create\")]\npublic sealed record CreateOrder : IRequest, IQueuable;")]
    [InlineData("PORTIA024",
        "[RequestRoute(\"*\", \"bad/area\", \"order\", \"create\")]\n[Discriminator(\"orders.create\")]\npublic sealed record CreateOrder : IRequest, IQueuable;")]
    [InlineData("PORTIA029",
        "[RequestRoute(\"*\", \"orders\", \"order\", \"create\")]\n[Discriminator(\"orders.create\")]\npublic sealed record CreateOrder : IRequest, IBadTransport;\n[RequestTransport(\"bad/id\")] public interface IBadTransport;")]
    public void ShouldHonorSuppressMessageGivenSuppressMessageWithoutRetainingSymbols(string id, string declaration)
    {
        // Arrange
        var source = $$"""
                                                             using Cntryl.Portia;
                                                             using System.Diagnostics.CodeAnalysis;
                                                             [SuppressMessage("Portia", "{{id}}")]
                                                             {{declaration}}
                                                             """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new PortiaServiceRegistrationGenerator());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == id);
    }

    [Fact]
    public void ShouldReportPortia021GivenDomainEventWithoutDiscriminator()
    {
        // Arrange
        const string source = """
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           public sealed record OrderCreated : DomainEvent;
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new DomainEventCatalogGenerator());

        // Assert
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA021");
    }

    [Fact]
    public void ShouldReportPortia024GivenUnsafeRequestRouteSegment()
    {
        // Arrange
        const string source = """
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           [RequestRoute("tenant/escape", "orders", "order", "create")]
                                                           [Discriminator("orders.create")]
                                                           public sealed record CreateOrder : IRequest, IQueuable;
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new PortiaServiceRegistrationGenerator());

        // Assert
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA024");
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad/id")]
    public void ShouldReportPortia029GivenInvalidTransportId(string id)
    {
        // Arrange
        var source = $$"""
                                                             using Cntryl.Portia;
                                                             [RequestTransport("{{id}}")] public interface IBadTransport;
                                                             [RequestRoute("*", "orders", "order", "create")]
                                                             [Discriminator("orders.create")]
                                                             public sealed record CreateOrder : IRequest, IBadTransport;
                                                             """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new PortiaServiceRegistrationGenerator());

        // Assert
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA029");
    }

    [Fact]
    public void ShouldReportPortia104GivenHandlerCatchAllReturningFailedResult()
    {
        // Arrange
        const string source = """
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           using System;
                                                           using System.Threading;
                                                           using System.Threading.Tasks;
                                                           public sealed record Request : IRequest;
                                                           public sealed class Handler : IRequestHandler<Request>
                                                           {
                                                               public async ValueTask<Result> HandleAsync(IRequestContext<Request> context, CancellationToken ct)
                                                               {
                                                                   try { await Task.Yield(); return Result.Success; }
                                                                   catch (Exception) { return Result.Failure(new(RequestErrorKind.Internal, "failed")); }
                                                               }
                                                           }
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer());

        // Assert
        _ = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "PORTIA104");
    }

    [Fact]
    public void ShouldIgnorePortia104GivenCatchAllsThatNarrowOrRethrowUnexpectedFailures()
    {
        // Arrange
        const string source = """
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           using System;
                                                           using System.Threading;
                                                           using System.Threading.Tasks;
                                                           public sealed record FilteredRequest : IRequest;
                                                           public sealed class FilteredHandler : IRequestHandler<FilteredRequest>
                                                           {
                                                               public async ValueTask<Result> HandleAsync(IRequestContext<FilteredRequest> context, CancellationToken ct)
                                                               {
                                                                   try { await Task.Yield(); return Result.Success; }
                                                                   catch (Exception ex) when (ex is InvalidOperationException)
                                                                   {
                                                                       return Result.Failure(new(RequestErrorKind.Validation, "expected"));
                                                                   }
                                                               }
                                                           }
                                                           public sealed record RethrowingRequest : IRequest;
                                                           public sealed class RethrowingHandler : IRequestHandler<RethrowingRequest>
                                                           {
                                                               public async ValueTask<Result> HandleAsync(IRequestContext<RethrowingRequest> context, CancellationToken ct)
                                                               {
                                                                   try { await Task.Yield(); return Result.Success; }
                                                                   catch (Exception ex)
                                                                   {
                                                                       if (ex is InvalidOperationException)
                                                                           return Result.Failure(new(RequestErrorKind.Validation, "expected"));
                                                                       throw;
                                                                   }
                                                               }
                                                           }
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA104");
    }

    [Fact]
    public void ShouldIgnorePortia104GivenFailureValuesThatAreNotReturned()
    {
        // Arrange
        const string source = """
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           using System;
                                                           using System.Threading;
                                                           using System.Threading.Tasks;
                                                           public sealed record Request : IRequest;
                                                           public sealed class Handler : IRequestHandler<Request>
                                                           {
                                                               public async ValueTask<Result> HandleAsync(IRequestContext<Request> context, CancellationToken ct)
                                                               {
                                                                   try { await Task.Yield(); return Result.Success; }
                                                                   catch (Exception)
                                                                   {
                                                                       _ = Result.Failure(new(RequestErrorKind.Internal, "telemetry value"));
                                                                       throw;
                                                                   }
                                                               }
                                                           }
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA104");
    }

    [Fact]
    public void ShouldIgnorePortia104GivenCatchesOutsideImplementedHandlerMethods()
    {
        // Arrange
        const string source = """
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           using System;
                                                           using System.Threading;
                                                           using System.Threading.Tasks;
                                                           public sealed record Request : IRequest;
                                                           public sealed class Handler : IRequestHandler<Request>
                                                           {
                                                               public ValueTask<Result> HandleAsync(IRequestContext<Request> context, CancellationToken ct) =>
                                                                   ValueTask.FromResult(Result.Success);

                                                               public Result Helper()
                                                               {
                                                                   try { throw new InvalidOperationException(); }
                                                                   catch (Exception) { return Result.Failure(new(RequestErrorKind.Internal, "failed")); }
                                                               }

                                                               private sealed class Nested
                                                               {
                                                                   public Result Helper()
                                                                   {
                                                                       try { throw new InvalidOperationException(); }
                                                                       catch (Exception) { return Result.Failure(new(RequestErrorKind.Internal, "failed")); }
                                                                   }
                                                               }
                                                           }
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA104");
    }

    [Fact]
    public void ShouldReportOncePerTypeGivenPartialDeclarations()
    {
        // Arrange
        const string source = """
                                                           using System;
                                                           using System.Threading;
                                                           using System.Threading.Tasks;
                                                           using Cntryl.Portia;
                                                           public sealed record First : IRequest;
                                                           public sealed record Second : IRequest;
                                                           public sealed partial class Handler(IServiceProvider services) : IRequestHandler<First>
                                                           {
                                                               public IServiceProvider Services { get; } = services;
                                                               public ValueTask<Result> HandleAsync(IRequestContext<First> context, CancellationToken ct) =>
                                                                   ValueTask.FromResult(Result.Success);
                                                           }
                                                           public sealed partial class Handler : IRequestHandler<Second>
                                                           {
                                                               public ValueTask<Result> HandleAsync(IRequestContext<Second> context, CancellationToken ct) =>
                                                                   ValueTask.FromResult(Result.Success);
                                                           }
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer());

        // Assert
        _ = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "PORTIA101");
        // One type may handle several requests, as a test double often does; that is a design choice, not a defect.
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA103");
    }

    /// <summary>
    ///     An editor analyzes one file at a time, so a constructor-parameter finding is reported from the partial
    ///     declaration that holds the parameter, and still once for the whole compilation.
    /// </summary>
    [Fact]
    public async Task ShouldReportFromPartialDeclarationGivenPartialHoldingTheParameter()
    {
        // Arrange
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var handlers = CSharpSyntaxTree.ParseText("""
                                                  using System.Threading;
                                                  using System.Threading.Tasks;
                                                  using Cntryl.Portia;
                                                  public sealed record Changed : DomainEvent;
                                                  public sealed partial class View : IProjectorHandler<Changed>
                                                  {
                                                      public ValueTask<Result> HandleAsync(IProjectorContext<Changed> context, CancellationToken ct) => default;
                                                  }
                                                  """, parseOptions, "View.Handlers.cs");
        const string state = """
                             using System.Net.Http;
                             using Cntryl.Portia;
                             public sealed partial class View(IProjectionStore store, HttpClient http)
                                 : Projector(store, EventStreamPattern.ForPattern("views"));
                             """;
        var stateTree = CSharpSyntaxTree.ParseText(state, parseOptions, "View.cs");
        var compilation = CSharpCompilation.Create("PartialFiles", [handlers, stateTree],
            ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(Aggregate).Assembly.Location)
            .Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var whole = await compilation.WithAnalyzers([new ComponentPracticeAnalyzer()]).GetAnalyzerDiagnosticsAsync();
        // A fresh analysis per file, as an editor runs for the open document.
        var inHandlers = await compilation.WithAnalyzers([new ComponentPracticeAnalyzer()])
            .GetAnalyzerSemanticDiagnosticsAsync(compilation.GetSemanticModel(handlers), null, CancellationToken.None);

        // Act
        var inState = await compilation.WithAnalyzers([new ComponentPracticeAnalyzer()])
            .GetAnalyzerSemanticDiagnosticsAsync(compilation.GetSemanticModel(stateTree), null, CancellationToken.None);

        // Assert
        Assert.Equal(["http"], Locations(state, whole.Where(diagnostic => diagnostic.Id == "PORTIA100")));
        Assert.Equal(["http"], Locations(state, inState.Where(diagnostic => diagnostic.Id == "PORTIA100")));
        Assert.DoesNotContain(inHandlers, diagnostic => diagnostic.Id == "PORTIA100");
    }

    [Fact]
    public void ShouldReportPracticeDiagnosticsGivenRecordStructComponents()
    {
        // Arrange
        const string source = """
                                                           using System;
                                                           using System.Threading;
                                                           using System.Threading.Tasks;
                                                           using Cntryl.Portia;
                                                           public sealed record Ping : IRequest;
                                                           public record struct StructHandler(IServiceProvider Services) : IRequestHandler<Ping>
                                                           {
                                                               public ValueTask<Result> HandleAsync(IRequestContext<Ping> context, CancellationToken ct) =>
                                                                   ValueTask.FromResult(Result.Success);
                                                           }
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer());

        // Assert
        _ = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "PORTIA101");
    }

    [Fact]
    public void ShouldReportPortia104GivenSemanticExceptionAndResultTypes()
    {
        // Arrange
        const string source = """
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           using System.Threading;
                                                           using System.Threading.Tasks;
                                                           public sealed record Request : IRequest;
                                                           public sealed class Exception : System.Exception;
                                                           public static class ResultFactory
                                                           {
                                                               public static Result Failure(RequestError error) => Result.Failure(error);
                                                           }
                                                           public sealed class Handler : IRequestHandler<Request>
                                                           {
                                                               public ValueTask<Result> HandleAsync(IRequestContext<Request> context, CancellationToken ct)
                                                               {
                                                                   try { throw new Exception(); }
                                                                   catch (Exception) { return ValueTask.FromResult(Result.Failure(new(RequestErrorKind.Internal, "expected"))); }
                                                               }
                                                           }
                                                           public sealed record OtherRequest : IRequest;
                                                           public sealed class OtherHandler : IRequestHandler<OtherRequest>
                                                           {
                                                               public ValueTask<Result> HandleAsync(IRequestContext<OtherRequest> context, CancellationToken ct)
                                                               {
                                                                   try { throw new System.Exception(); }
                                                                   catch (System.Exception) { return ValueTask.FromResult(ResultFactory.Failure(new(RequestErrorKind.Internal, "failed"))); }
                                                               }
                                                           }
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA104");
    }

    [Fact]
    public void ShouldReportPortia107GivenScenarioExpectationsDiscardedAsStatements()
    {
        // Arrange
        const string source = """
                                                           using System;
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           public sealed record Request : IRequest;
                                                           public static class Tests
                                                           {
                                                               public static void Forgotten(IServiceProvider services) =>
                                                                   RequestScenario.For(services).When(new Request()).ExpectDenied();
                                                           }
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ScenarioObservationAnalyzer());

        // Assert
        _ = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "PORTIA107");
    }

    [Fact]
    public void ShouldIgnorePortia107GivenAwaitedOrDeliberatelyKeptExpectations()
    {
        // Arrange
        const string source = """
                                                           using System;
                                                           using System.Threading.Tasks;
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           public sealed record Request : IRequest;
                                                           public static class Tests
                                                           {
                                                               public static async Task Observed(IServiceProvider services)
                                                               {
                                                                   await RequestScenario.For(services).When(new Request()).ExpectDenied();
                                                                   var kept = RequestScenario.For(services).When(new Request());
                                                                   kept = kept.ExpectHandled();
                                                                   await kept;
                                                               }
                                                           }
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ScenarioObservationAnalyzer());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA107");
    }

    [Fact]
    public void ShouldIgnorePortia105And106GivenPreflightReadsThroughNonPortiaClients()
    {
        // Arrange
        const string source = """
                                                           using System.Data.Common;
                                                           using System.Net.Http;
                                                           using System.Threading;
                                                           using System.Threading.Tasks;
                                                           using Cntryl.Portia;
                                                           public sealed record Req : IRequest;
                                                           public sealed class PolicyAuthorizer(HttpClient policy) : IRequestAuthorizer<Req>
                                                           {
                                                               public ValueTask<Result> AuthorizeAsync(IRequestContext<Req> context, CancellationToken ct) => default;
                                                           }
                                                           public sealed class UniqueGuard(DbConnection readModel) : IRequestGuard<Req>
                                                           {
                                                               public ValueTask<Result> GuardAsync(IRequestContext<Req> context, CancellationToken ct) => default;
                                                           }
                                                           public interface IAccountRepository : IProjectionStore
                                                           {
                                                               ValueTask<int> GetBalanceAsync(CancellationToken ct);
                                                           }
                                                           public sealed class BalanceGuard(IAccountRepository accounts) : IRequestGuard<Req>
                                                           {
                                                               public ValueTask<Result> GuardAsync(IRequestContext<Req> context, CancellationToken ct) => default;
                                                           }
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "PORTIA105" or "PORTIA106");
    }

    [Fact]
    public void ShouldReportPortia105GivenPreflightDispatchAndProjectionStore()
    {
        // Arrange
        const string source = """
                                                           using System.Threading;
                                                           using System.Threading.Tasks;
                                                           using Cntryl.Portia;
                                                           public sealed record Req : IRequest;
                                                           public sealed class DispatchingGuard(IRequestBus bus, IProjectionStore store) : IRequestGuard<Req>
                                                           {
                                                               public ValueTask<Result> GuardAsync(IRequestContext<Req> context, CancellationToken ct) => default;
                                                           }
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer());

        // Assert
        Assert.Equal(2, diagnostics.Count(diagnostic => diagnostic.Id == "PORTIA105"));
    }

    [Theory]
    [InlineData("PORTIA105", "IRequestGuard<Req>", "GuardAsync", "IProjectionStore")]
    [InlineData("PORTIA106", "IRequestAuthorizer<Req>", "AuthorizeAsync", "IProjectionStore")]
    [InlineData("PORTIA105", "IRequestGuard<Req>", "GuardAsync", "IProjectionCheckpointStore")]
    [InlineData("PORTIA106", "IRequestAuthorizer<Req>", "AuthorizeAsync", "IProjectionCheckpointStore")]
    public void ShouldSuggestQueryOnlyInterfaceGivenPreflightProjectionStoreDiagnostic(
        string diagnosticId, string role, string method, string dependencyType)
    {
        // Arrange
        var source = $$"""
                                                            using System.Threading;
                                                            using System.Threading.Tasks;
                                                            using Cntryl.Portia;
                                                            public sealed record Req : IRequest;
                                                            public sealed class Preflight({{dependencyType}} store) : {{role}}
                                                            {
                                                                public ValueTask<Result> {{method}}(
                                                                    IRequestContext<Req> context, CancellationToken ct) => default;
                                                            }
                                                            """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer());

        // Assert
        var diagnostic = Assert.Single(diagnostics, item => item.Id == diagnosticId);
        Assert.Contains("inject an application-owned query interface that exposes only the read this preflight needs",
            diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldIgnorePortia100GivenProjectorsOwnProjectionStore()
    {
        // Arrange
        const string source = """
                                                           using System.Threading;
                                                           using System.Threading.Tasks;
                                                           using Cntryl.Portia;
                                                           namespace Microsoft.EntityFrameworkCore { public abstract class DbContext; }
                                                           public abstract class AccountsDb : Microsoft.EntityFrameworkCore.DbContext, IProjectionStore
                                                           {
                                                               public abstract ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity, CancellationToken ct = default);
                                                               public abstract ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default);
                                                           }
                                                           public sealed class AccountProjector(AccountsDb db) : Projector(db, EventStreamPattern.ForPattern("events"));
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA100");
    }

    [Fact]
    public void ShouldIgnorePortia104GivenFailuresBuiltInsideLambdas()
    {
        // Arrange
        const string source = """
                                                           using System;
                                                           using System.Threading;
                                                           using System.Threading.Tasks;
                                                           using Cntryl.Portia;
                                                           public sealed record Req : IRequest;
                                                           public sealed class Handler : IRequestHandler<Req>
                                                           {
                                                               public async ValueTask<Result> HandleAsync(IRequestContext<Req> context, CancellationToken ct)
                                                               {
                                                                   try { await Task.Yield(); return Result.Success; }
                                                                   catch (Exception ex) { return Fallback(() => Result.Failure(new(RequestErrorKind.Internal, "failed")), ex); }
                                                               }
                                                               static Result Fallback(Func<Result> fallback, Exception ex) => throw ex;
                                                           }
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA104");
    }

    [Fact]
    public void ShouldLeaveLocatorsToPortia102GivenAggregateLocatorsForPortia101()
    {
        // Arrange
        const string source = """
                                                           using System;
                                                           using Cntryl.Portia;
                                                           public sealed class Account(Uuid id, IServiceProvider services)
                                                               : Aggregate(id, new EventStreamAddress("r", "a", id.ToString()));
                                                           """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA101");
        _ = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "PORTIA102");
    }
}
