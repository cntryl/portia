using Microsoft.CodeAnalysis;

namespace Cntryl.Portia.Consumer;

/// <summary>
///     Declarations the generators cannot name from generated code, or values they must re-emit as
///     C# source, have to end in a Portia diagnostic or correct output — never in generated source
///     that fails to compile with a compiler error pointing into a file the application never wrote.
/// </summary>
public sealed class GeneratorInvalidShapeTests
{
    [Fact]
    public void ShouldSkipEventGivenInaccessibleContainingTypeWhenAddingPortia()
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics("""
                                                                 using Cntryl.Portia;
                                                                 using Microsoft.Extensions.DependencyInjection;
                                                                 public static class Outer
                                                                 {
                                                                     private static class Events
                                                                     {
                                                                         [Discriminator("app.ev")]
                                                                         public sealed record Ev : DomainEvent;
                                                                     }
                                                                 }
                                                                 public static class Scenario
                                                                 {
                                                                     public static PortiaBuilder Register(IServiceCollection services) => services.AddPortia();
                                                                 }
                                                                 """, new RegistrationCallInterceptorGenerator(),
            new DomainEventCatalogGenerator());

        AssertNoCompilerErrors(diagnostics);
    }

    [Fact]
    public void ShouldSkipEventGivenFileLocalEventWhenAddingPortia()
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics("""
                                                                 using Cntryl.Portia;
                                                                 using Microsoft.Extensions.DependencyInjection;
                                                                 [Discriminator("app.ev")]
                                                                 file sealed record Ev : DomainEvent;
                                                                 public static class Scenario
                                                                 {
                                                                     public static PortiaBuilder Register(IServiceCollection services) => services.AddPortia();
                                                                 }
                                                                 """, new RegistrationCallInterceptorGenerator(),
            new DomainEventCatalogGenerator());

        AssertNoCompilerErrors(diagnostics);
    }

    [Fact]
    public void ShouldReportPortia015GivenFileLocalHandlerWhenRegistered()
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics("""
                                                                 using System.Threading;
                                                                 using System.Threading.Tasks;
                                                                 using Cntryl.Portia;
                                                                 using Microsoft.Extensions.DependencyInjection;
                                                                 public sealed record Ping : IRequest;
                                                                 file sealed class Handler : IRequestHandler<Ping>
                                                                 {
                                                                     public ValueTask<Result> HandleAsync(IRequestContext<Ping> context, CancellationToken ct) =>
                                                                         ValueTask.FromResult(Result.Success);
                                                                 }
                                                                 public static class Scenario
                                                                 {
                                                                     public static PortiaBuilder Register(IServiceCollection services) =>
                                                                         services.AddPortia().AddRequestHandler<Handler>();
                                                                 }
                                                                 """, new RegistrationCallInterceptorGenerator(),
            new RequestShapeDiagnosticsGenerator(), new PortiaServiceRegistrationGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA015");
        AssertNoCompilerErrors(diagnostics);
    }

    [Theory]
    [InlineData("file sealed record CreateOrder : IRequest, IQueuable;")]
    [InlineData("public static class Outer { [RequestRoute(\"app\", \"orders\", \"order\", \"create\")] [Discriminator(\"app.orders.create\")] private sealed record CreateOrder : IRequest, IQueuable; }")]
    public void ShouldReportPortia015GivenInaccessibleTransportedRequest(string declaration)
    {
        var attributes = declaration.StartsWith("file", StringComparison.Ordinal)
            ? "[RequestRoute(\"app\", \"orders\", \"order\", \"create\")] [Discriminator(\"app.orders.create\")] "
            : string.Empty;
        var diagnostics = GeneratorCompilation.OutputDiagnostics("using Cntryl.Portia;\n" + attributes + declaration,
            new PortiaServiceRegistrationGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA015");
        AssertNoCompilerErrors(diagnostics);
    }

    [Fact]
    public void ShouldReportPortia015GivenFileLocalProjector()
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics("""
                                                                 using System.Threading;
                                                                 using System.Threading.Tasks;
                                                                 using Cntryl.Portia;
                                                                 [Discriminator("app.ev")]
                                                                 public sealed record Ev : DomainEvent;
                                                                 file sealed partial class View(IProjectionStore store)
                                                                     : Projector(store, EventStreamPattern.ForPattern("test")), IProjectorHandler<Ev>
                                                                 {
                                                                     public ValueTask HandleAsync(Ev ev, IProjectorContext context, CancellationToken ct) =>
                                                                         ValueTask.CompletedTask;
                                                                 }
                                                                 """, new ProjectorReactorEventDispatcherGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA015");
        AssertNoCompilerErrors(diagnostics);
    }

    [Fact]
    public void ShouldReportPortia016GivenFileLocalHttpRequest()
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics("""
                                                                 using Cntryl.Portia;
                                                                 using Microsoft.AspNetCore.Routing;
                                                                 file sealed record GetOrder(int Id) : IRequest, ICallable;
                                                                 public static class Endpoints
                                                                 {
                                                                     public static void Map(IEndpointRouteBuilder app) => app.MapPortiaGet<GetOrder>("/orders/{id}");
                                                                 }
                                                                 """, new RequestHttpBindingGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA016");
        AssertNoCompilerErrors(diagnostics);
    }

    [Fact]
    public void ShouldEmitDistinctRegistrationNamesGivenQualifiedNameCollidingWithSimpleName()
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics("""
                                                                 using Cntryl.Portia;
                                                                 namespace Billing
                                                                 {
                                                                     [RequestRoute("app", "billing", "charge", "create")]
                                                                     [Discriminator("billing.charge")]
                                                                     public sealed record Charge : IRequest, IQueuable;
                                                                 }
                                                                 namespace Sales
                                                                 {
                                                                     [RequestRoute("app", "sales", "charge", "create")]
                                                                     [Discriminator("sales.charge")]
                                                                     public sealed record Charge : IRequest, IQueuable;
                                                                 }
                                                                 namespace Other
                                                                 {
                                                                     [RequestRoute("app", "other", "charge", "create")]
                                                                     [Discriminator("other.charge")]
                                                                     public sealed record BillingCharge : IRequest, IQueuable;
                                                                 }
                                                                 """, new PortiaServiceRegistrationGenerator());

        AssertNoCompilerErrors(diagnostics);
    }

    [Fact]
    public void ShouldEscapeControlCharactersGivenDiscriminatorAndPermissionLiterals()
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics("""
                                                                 using System.Threading;
                                                                 using System.Threading.Tasks;
                                                                 using Cntryl.Portia;
                                                                 using Microsoft.Extensions.DependencyInjection;
                                                                 [Discriminator("app\nev")]
                                                                 public sealed record Ev : DomainEvent;
                                                                 [RequiresPermission("a\n{Id}")]
                                                                 public sealed record Read(string Id) : IRequest;
                                                                 public sealed class Handler : IRequestHandler<Read>
                                                                 {
                                                                     public ValueTask<Result> HandleAsync(IRequestContext<Read> context, CancellationToken ct) =>
                                                                         ValueTask.FromResult(Result.Success);
                                                                 }
                                                                 public static class Scenario
                                                                 {
                                                                     public static PortiaBuilder Register(IServiceCollection services) =>
                                                                         services.AddPortia().AddRequestHandler<Handler>();
                                                                 }
                                                                 """, new RegistrationCallInterceptorGenerator(),
            new DomainEventCatalogGenerator());

        AssertNoCompilerErrors(diagnostics);
    }

    [Fact]
    public void ShouldReportPortia011GivenPermissionTokenBoundToInaccessibleProperty()
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics("""
                                                                 using System.Threading;
                                                                 using System.Threading.Tasks;
                                                                 using Cntryl.Portia;
                                                                 using Microsoft.Extensions.DependencyInjection;
                                                                 [RequiresPermission("accounts:{Secret}:read")]
                                                                 public sealed record Read(string Id) : IRequest
                                                                 {
                                                                     private string Secret => Id;
                                                                 }
                                                                 public sealed class Handler : IRequestHandler<Read>
                                                                 {
                                                                     public ValueTask<Result> HandleAsync(IRequestContext<Read> context, CancellationToken ct) =>
                                                                         ValueTask.FromResult(Result.Success);
                                                                 }
                                                                 public static class Scenario
                                                                 {
                                                                     public static PortiaBuilder Register(IServiceCollection services) =>
                                                                         services.AddPortia().AddRequestHandler<Handler>();
                                                                 }
                                                                 """, new RegistrationCallInterceptorGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA011");
        AssertNoCompilerErrors(diagnostics);
    }

    [Fact]
    public void ShouldReportPortia011GivenInternalGetterInReferencedAssembly()
    {
        var reference = GeneratorCompilation.Reference("""
                                                       namespace Shared;
                                                       public abstract record RequestBase
                                                       {
                                                           internal string Secret => "hidden";
                                                       }
                                                       """);
        var diagnostics = GeneratorCompilation.OutputDiagnostics("""
                                                                 using System.Threading;
                                                                 using System.Threading.Tasks;
                                                                 using Cntryl.Portia;
                                                                 using Microsoft.Extensions.DependencyInjection;
                                                                 [RequiresPermission("accounts:{Secret}:read")]
                                                                 public sealed record Read(string Id) : Shared.RequestBase, IRequest;
                                                                 public sealed class Handler : IRequestHandler<Read>
                                                                 {
                                                                     public ValueTask<Result> HandleAsync(IRequestContext<Read> context, CancellationToken ct) =>
                                                                         ValueTask.FromResult(Result.Success);
                                                                 }
                                                                 public static class Scenario
                                                                 {
                                                                     public static PortiaBuilder Register(IServiceCollection services) =>
                                                                         services.AddPortia().AddRequestHandler<Handler>();
                                                                 }
                                                                 """, [reference], new RegistrationCallInterceptorGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA011");
        AssertNoCompilerErrors(diagnostics);
    }

    [Fact]
    public void ShouldEmitNamedConstantsGivenNonFiniteFloatingPointDefaults()
    {
        var diagnostics = GeneratorCompilation.OutputDiagnostics("""
                                                                 using Cntryl.Portia;
                                                                 using Microsoft.AspNetCore.Routing;
                                                                 public sealed record Probe(
                                                                     double NotANumber = double.NaN,
                                                                     double Positive = double.PositiveInfinity,
                                                                     double Negative = double.NegativeInfinity,
                                                                     float Single = float.NaN,
                                                                     float SinglePositive = float.PositiveInfinity,
                                                                     float SingleNegative = float.NegativeInfinity) : IRequest, ICallable;
                                                                 public static class Endpoints
                                                                 {
                                                                     public static void Map(IEndpointRouteBuilder app) => app.MapPortiaGet<Probe>("/probe");
                                                                 }
                                                                 """, new RequestHttpBindingGenerator());

        AssertNoCompilerErrors(diagnostics);
    }

    static void AssertNoCompilerErrors(IReadOnlyList<Diagnostic> diagnostics)
    {
        var errors = diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error
                                                     && diagnostic.Id.StartsWith("CS", StringComparison.Ordinal))
            .ToArray();
        Assert.True(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
    }
}
