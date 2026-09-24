using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cntryl.Portia.Consumer;

public sealed class GeneratorDiagnosticsTests
{
    [Fact]
    public void Portia100RecognizesKnownEffectDependenciesThroughTypeAncestry()
    {
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

        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer())
            .Where(diagnostic => diagnostic.Id == "PORTIA100").ToArray();

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
    public void Portia100ReportsProjectorAggregateWriterAndExecutorButNotReader()
    {
        const string source = """
                              using Cntryl.Portia;
                              public sealed class Projection(IAggregateWriter writer, IAggregateExecutor executor, IAggregateReader reader)
                                  : Projector(null!, EventStreamPattern.ForPattern("events"));
                              """;

        var locations = Locations(source, GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer())
            .Where(diagnostic => diagnostic.Id == "PORTIA100"));

        Assert.Equal(["executor", "writer"], locations);
    }

    [Fact]
    public void Portia102ReportsAggregateReaderWriterAndExecutorDependencies()
    {
        const string source = """
                              using Cntryl.Portia;
                              public sealed class Account(IAggregateReader reader, IAggregateWriter writer, IAggregateExecutor executor)
                                  : Aggregate(Uuid.CreateVersion4(), new EventStreamAddress("bank", "accounts", "one"));
                              """;

        var locations = Locations(source, GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer())
            .Where(diagnostic => diagnostic.Id == "PORTIA102"));

        Assert.Equal(["executor", "reader", "writer"], locations);
    }

    [Theory]
    [InlineData("PORTIA105", "IRequestGuard<Command>",
        "public ValueTask<Result> GuardAsync(IRequestContext<Command> context, CancellationToken ct) => default;",
        "Request guard 'Component' takes effect-capable dependency '")]
    [InlineData("PORTIA106", "IRequestAuthorizer<Command>",
        "public ValueTask<Result> AuthorizeAsync(IRequestContext<Command> context, CancellationToken ct) => default;",
        "Request authorizer 'Component' takes effect-capable dependency '")]
    public void Portia105And106ReportPreflightEffectCapableDependencies(string id, string role, string member,
        string messagePrefix)
    {
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

        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer())
            .Where(diagnostic => diagnostic.Id == id).ToArray();

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
    public void Portia105And106AllowReadOnlyDependencies(string role, string member)
    {
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

        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "PORTIA105" or "PORTIA106");
    }

    [Fact]
    public void Portia105And106IgnoreTypesThatAreNotGuardsOrAuthorizers()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           public sealed class ApplicationService(IRequestBus bus, IAggregateWriter writer);
                                                           """, new ComponentPracticeAnalyzer());

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
    public void Portia101ReportsConstructorAndActivatorUtilitiesServiceLocationAtUse()
    {
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

        var diagnostics = GeneratorCompilation.Diagnostics(source, new ComponentPracticeAnalyzer())
            .Where(diagnostic => diagnostic.Id == "PORTIA101").ToArray();

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
    public void PracticeDiagnosticsRemainBestEffortAndIgnoreUnrecognizedGatewaysAndLookalikes()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
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
                                                           """, new ComponentPracticeAnalyzer());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "PORTIA100" or "PORTIA101");
    }

    [Fact]
    public void BatchHandlerWithoutBatchBaseNamesProcessorAndHandlerAtProcessorDeclaration()
    {
        const string source = """
                              using Cntryl.Portia;
                              public sealed record Changed : DomainEvent;
                              public sealed partial class Projection
                                  : Projector(null!, EventStreamPattern.ForPattern("events")), IBatchProjectorHandler<Changed>;
                              """;

        var diagnostic = Assert.Single(GeneratorCompilation.Diagnostics(source,
            new ProjectorReactorEventDispatcherGenerator()), item => item.Id == "PORTIA017");

        Assert.Equal("Processor 'Projection' must use a batch base to implement handler " +
                     "'Cntryl.Portia.IBatchProjectorHandler<Changed>'",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.Equal("Projection", source.Substring(diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length));
    }

    [Fact]
    public void SingleAndBatchHandlerForOneEventNamesProcessorAndEventAtProcessorDeclaration()
    {
        const string source = """
                              using Cntryl.Portia;
                              public sealed record Changed : DomainEvent;
                              public sealed partial class Projection
                                  : BatchProjector(null!, EventStreamPattern.ForPattern("events")),
                                    IProjectorHandler<Changed>, IBatchProjectorHandler<Changed>;
                              """;

        var diagnostic = Assert.Single(GeneratorCompilation.Diagnostics(source,
            new ProjectorReactorEventDispatcherGenerator()), item => item.Id == "PORTIA028");

        Assert.Equal("Processor 'Projection' selects both single and batch handling for event 'Changed'",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.Equal("Projection", source.Substring(diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length));
    }

    [Fact]
    public void DuplicateDomainEventNamesBothClrTypesAtDuplicateDeclaration()
    {
        const string source = """
                              using Cntryl.Portia;
                              [Discriminator("changed", 2)]
                              public sealed record Original : DomainEvent;
                              [Discriminator("changed", 2)]
                              public sealed record Duplicate : DomainEvent;
                              """;

        var diagnostic = Assert.Single(GeneratorCompilation.Diagnostics(source,
            new DomainEventCatalogGenerator()), item => item.Id == "PORTIA023");

        Assert.Equal("Domain-event CLR types 'Original' and 'Duplicate' both declare discriminator 'changed' " +
                     "version 2", diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.Equal("Duplicate", source.Substring(diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length));
    }

    [Fact]
    public void InferredExternalDomainEventLocalsDoNotReportDuplicateDiscriminator()
    {
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

        var diagnostics = GeneratorCompilation.Diagnostics(source, [contracts],
            new DomainEventCatalogGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA023");
    }

    [Fact]
    public void ExplicitBaseTypedExternalDomainEventLocalsDoNotReportDuplicateDiscriminator()
    {
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

        var diagnostics = GeneratorCompilation.Diagnostics(source, [contracts],
            new DomainEventCatalogGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA023");
    }

    [Fact]
    public void DistinctExternalDomainEventTypesStillReportDuplicateDiscriminator()
    {
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

        var diagnostic = Assert.Single(GeneratorCompilation.Diagnostics(source, [contracts],
            new DomainEventCatalogGenerator()), item => item.Id == "PORTIA023");

        Assert.Equal("Domain-event CLR types 'Contracts.BoundaryDraftCreated' and " +
                     "'Contracts.BoundaryDraftRevised' both declare discriminator 'boundary.draft.changed' version 1",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void DuplicateRequestNamesBothClrTypesAtDuplicateDeclaration()
    {
        const string source = """
                              using Cntryl.Portia;
                              [RequestRoute("*", "orders", "order", "create")]
                              [Discriminator("orders.create", 2)]
                              public sealed record Original : IRequest, IQueuable;
                              [RequestRoute("*", "orders", "order", "duplicate")]
                              [Discriminator("orders.create", 2)]
                              public sealed record Duplicate : IRequest, IQueuable;
                              """;

        var diagnostic = Assert.Single(GeneratorCompilation.Diagnostics(source,
            new PortiaServiceRegistrationGenerator()), item => item.Id == "PORTIA022");

        Assert.Equal("Request CLR types 'Original' and 'Duplicate' both declare discriminator 'orders.create' " +
                     "version 2", diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.Equal("Duplicate", source.Substring(diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length));
    }

    [Fact]
    public void DuplicateRequestDiagnosticMovesWithDeclarationOnIncrementalRerun()
    {
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
            [new PortiaServiceRegistrationGenerator().AsSourceGenerator()], parseOptions: parseOptions);
        driver = driver.RunGenerators(compilation);

        var movedSource = Environment.NewLine + source;
        var movedTree = CSharpSyntaxTree.ParseText(movedSource, parseOptions,
            "Requests.cs");
        driver = driver.RunGenerators(compilation.ReplaceSyntaxTree(tree, movedTree));

        var diagnostic = Assert.Single(driver.GetRunResult().Diagnostics, item => item.Id == "PORTIA022");
        Assert.Equal("Duplicate", movedSource.Substring(diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length));
        Assert.Equal(7, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public void Portia015ReportsGenericHandler()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           using System.Threading;
                                                           using System.Threading.Tasks;
                                                           public sealed record Request : IRequest;
                                                           public class Handler<T> : IRequestHandler<Request>
                                                           {
                                                               public ValueTask<Result> HandleAsync(IRequestContext<Request> c, CancellationToken ct) => ValueTask.FromResult(Result.Success);
                                                           }
                                                           """, new RequestShapeAnalyzer());
        var diagnostic = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "PORTIA015");
        Assert.Equal("Component 'Handler<T>' cannot be generated: generic component types are unsupported; " +
                     "use a closed, non-generic component class",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Portia020ReportsTransportedRequestWithoutDiscriminator()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           [RequestRoute("*", "orders", "order", "create")]
                                                           public sealed record CreateOrder : IRequest, IQueuable;
                                                           """, new PortiaServiceRegistrationGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA020");
    }

    [Theory]
    [InlineData("PORTIA020",
        "[RequestRoute(\"*\", \"orders\", \"order\", \"create\")]\npublic sealed record CreateOrder : IRequest, IQueuable;")]
    [InlineData("PORTIA024",
        "[RequestRoute(\"*\", \"bad/area\", \"order\", \"create\")]\n[Discriminator(\"orders.create\")]\npublic sealed record CreateOrder : IRequest, IQueuable;")]
    [InlineData("PORTIA029",
        "[RequestRoute(\"*\", \"orders\", \"order\", \"create\")]\n[Discriminator(\"orders.create\")]\npublic sealed record CreateOrder : IRequest, IBadTransport;\n[RequestTransport(\"bad/id\")] public interface IBadTransport;")]
    public void PortiaDiagnosticsHonorPragmaSuppressionWithoutRetainingSyntaxTrees(string id, string declaration)
    {
        var diagnostics = GeneratorCompilation.Diagnostics($$"""
                                                             using Cntryl.Portia;
                                                             #pragma warning disable {{id}}
                                                             {{declaration}}
                                                             #pragma warning restore {{id}}
                                                             """, new PortiaServiceRegistrationGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == id);
    }

    [Theory]
    [InlineData("PORTIA020",
        "[RequestRoute(\"*\", \"orders\", \"order\", \"create\")]\npublic sealed record CreateOrder : IRequest, IQueuable;")]
    [InlineData("PORTIA024",
        "[RequestRoute(\"*\", \"bad/area\", \"order\", \"create\")]\n[Discriminator(\"orders.create\")]\npublic sealed record CreateOrder : IRequest, IQueuable;")]
    [InlineData("PORTIA029",
        "[RequestRoute(\"*\", \"orders\", \"order\", \"create\")]\n[Discriminator(\"orders.create\")]\npublic sealed record CreateOrder : IRequest, IBadTransport;\n[RequestTransport(\"bad/id\")] public interface IBadTransport;")]
    public void PortiaDiagnosticsIgnoreInactivePragmaDisable(string id, string declaration)
    {
        var diagnostics = GeneratorCompilation.Diagnostics($$"""
                                                             using Cntryl.Portia;
                                                             #if false
                                                             #pragma warning disable {{id}}
                                                             #endif
                                                             {{declaration}}
                                                             """, new PortiaServiceRegistrationGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == id);
    }

    [Theory]
    [InlineData("PORTIA020",
        "[RequestRoute(\"*\", \"orders\", \"order\", \"create\")]\npublic sealed record CreateOrder : IRequest, IQueuable;")]
    [InlineData("PORTIA024",
        "[RequestRoute(\"*\", \"bad/area\", \"order\", \"create\")]\n[Discriminator(\"orders.create\")]\npublic sealed record CreateOrder : IRequest, IQueuable;")]
    [InlineData("PORTIA029",
        "[RequestRoute(\"*\", \"orders\", \"order\", \"create\")]\n[Discriminator(\"orders.create\")]\npublic sealed record CreateOrder : IRequest, IBadTransport;\n[RequestTransport(\"bad/id\")] public interface IBadTransport;")]
    public void PortiaDiagnosticsIgnoreInactivePragmaRestore(string id, string declaration)
    {
        var diagnostics = GeneratorCompilation.Diagnostics($$"""
                                                             using Cntryl.Portia;
                                                             #pragma warning disable {{id}}
                                                             #if false
                                                             #pragma warning restore {{id}}
                                                             #endif
                                                             {{declaration}}
                                                             """, new PortiaServiceRegistrationGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == id);
    }

    [Theory]
    [InlineData("PORTIA020",
        "[RequestRoute(\"*\", \"orders\", \"order\", \"create\")]\npublic sealed record CreateOrder : IRequest, IQueuable;")]
    [InlineData("PORTIA024",
        "[RequestRoute(\"*\", \"bad/area\", \"order\", \"create\")]\n[Discriminator(\"orders.create\")]\npublic sealed record CreateOrder : IRequest, IQueuable;")]
    [InlineData("PORTIA029",
        "[RequestRoute(\"*\", \"orders\", \"order\", \"create\")]\n[Discriminator(\"orders.create\")]\npublic sealed record CreateOrder : IRequest, IBadTransport;\n[RequestTransport(\"bad/id\")] public interface IBadTransport;")]
    public void PortiaDiagnosticsHonorSuppressMessageWithoutRetainingSymbols(string id, string declaration)
    {
        var diagnostics = GeneratorCompilation.Diagnostics($$"""
                                                             using Cntryl.Portia;
                                                             using System.Diagnostics.CodeAnalysis;
                                                             [SuppressMessage("Portia", "{{id}}")]
                                                             {{declaration}}
                                                             """, new PortiaServiceRegistrationGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == id);
    }

    [Fact]
    public void Portia021ReportsDomainEventWithoutDiscriminator()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           public sealed record OrderCreated : DomainEvent;
                                                           """, new DomainEventCatalogGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA021");
    }

    [Fact]
    public void Portia024ReportsUnsafeRequestRouteSegment()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           [RequestRoute("tenant/escape", "orders", "order", "create")]
                                                           [Discriminator("orders.create")]
                                                           public sealed record CreateOrder : IRequest, IQueuable;
                                                           """, new PortiaServiceRegistrationGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA024");
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad/id")]
    public void Portia029ReportsInvalidTransportId(string id)
    {
        var diagnostics = GeneratorCompilation.Diagnostics($$"""
                                                             using Cntryl.Portia;
                                                             [RequestTransport("{{id}}")] public interface IBadTransport;
                                                             [RequestRoute("*", "orders", "order", "create")]
                                                             [Discriminator("orders.create")]
                                                             public sealed record CreateOrder : IRequest, IBadTransport;
                                                             """, new PortiaServiceRegistrationGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA029");
    }

    [Fact]
    public void Portia104ReportsHandlerCatchAllReturningFailedResult()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
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
                                                           """, new ComponentPracticeAnalyzer());

        _ = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "PORTIA104");
    }

    [Fact]
    public void Portia104IgnoresCatchAllsThatNarrowOrRethrowUnexpectedFailures()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
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
                                                           """, new ComponentPracticeAnalyzer());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA104");
    }

    [Fact]
    public void Portia104IgnoresFailureValuesThatAreNotReturned()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
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
                                                           """, new ComponentPracticeAnalyzer());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA104");
    }

    [Fact]
    public void Portia104IgnoresCatchesOutsideImplementedHandlerMethods()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
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
                                                           """, new ComponentPracticeAnalyzer());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA104");
    }

    [Fact]
    public void PracticeDiagnosticsReportOncePerTypeGivenPartialDeclarations()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
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
                                                           """, new ComponentPracticeAnalyzer());

        _ = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "PORTIA101");
        // One type may handle several requests, as a test double often does; that is a design choice, not a defect.
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA103");
    }

    [Fact]
    public void Portia104UsesSemanticExceptionAndResultTypes()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
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
                                                           """, new ComponentPracticeAnalyzer());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA104");
    }

    [Fact]
    public void Portia107ReportsScenarioExpectationsDiscardedAsStatements()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using System;
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           public sealed record Request : IRequest;
                                                           public static class Tests
                                                           {
                                                               public static void Forgotten(IServiceProvider services) =>
                                                                   RequestScenario.For(services).When(new Request()).ExpectDenied();
                                                           }
                                                           """, new ScenarioObservationAnalyzer());

        _ = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "PORTIA107");
    }

    [Fact]
    public void Portia107IgnoresAwaitedAndDeliberatelyKeptExpectations()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
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
                                                                   _ = kept.ExpectHandled();
                                                                   await kept;
                                                               }
                                                           }
                                                           """, new ScenarioObservationAnalyzer());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA107");
    }

    [Fact]
    public void Portia105And106IgnorePreflightReadsThroughNonPortiaClients()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
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
                                                           """, new ComponentPracticeAnalyzer());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "PORTIA105" or "PORTIA106");
    }

    [Fact]
    public void Portia105StillReportsPreflightDispatchAndProjectionStore()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using System.Threading;
                                                           using System.Threading.Tasks;
                                                           using Cntryl.Portia;
                                                           public sealed record Req : IRequest;
                                                           public sealed class DispatchingGuard(IRequestBus bus, IProjectionStore store) : IRequestGuard<Req>
                                                           {
                                                               public ValueTask<Result> GuardAsync(IRequestContext<Req> context, CancellationToken ct) => default;
                                                           }
                                                           """, new ComponentPracticeAnalyzer());

        Assert.Equal(2, diagnostics.Count(diagnostic => diagnostic.Id == "PORTIA105"));
    }

    [Fact]
    public void Portia100IgnoresTheProjectorsOwnProjectionStore()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
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
                                                           """, new ComponentPracticeAnalyzer());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA100");
    }

    [Fact]
    public void Portia104IgnoresFailuresBuiltInsideLambdas()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
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
                                                           """, new ComponentPracticeAnalyzer());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA104");
    }

    [Fact]
    public void Portia101LeavesAggregateLocatorsToPortia102()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using System;
                                                           using Cntryl.Portia;
                                                           public sealed class Account(Uuid id, IServiceProvider services)
                                                               : Aggregate(id, new EventStreamAddress("r", "a", id.ToString()));
                                                           """, new ComponentPracticeAnalyzer());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA101");
        _ = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "PORTIA102");
    }
}
