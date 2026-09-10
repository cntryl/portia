namespace Cntryl.Portia.Consumer;

public sealed class GeneratorDiagnosticsTests
{
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
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
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
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
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
                     "version 2", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("Duplicate", source.Substring(diagnostic.Location.SourceSpan.Start,
            diagnostic.Location.SourceSpan.Length));
    }

    [Fact]
    public void GenericHandlerHasActionableDiagnostic()
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
                                                           """, new RequestShapeDiagnosticsGenerator());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA015");
    }

    [Fact]
    public void TransportedRequestWithoutDiscriminatorHasActionableDiagnostic()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           [RequestRoute("*", "orders", "order", "create")]
                                                           public sealed record CreateOrder : IRequest, IQueuable;
                                                           """, new PortiaServiceRegistrationGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA020");
    }

    [Fact]
    public void DomainEventWithoutDiscriminatorHasActionableDiagnostic()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
                                                           public sealed record OrderCreated : DomainEvent;
                                                           """, new DomainEventCatalogGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA021");
    }

    [Fact]
    public void UnsafeRequestRouteSegmentHasActionableDiagnostic()
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

    [Fact]
    public void HandlerCatchAllReturningFailedResultHasActionableDiagnostic()
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
                                                           """, new ComponentPracticeGenerator());

        _ = Assert.Single(diagnostics, diagnostic => diagnostic.Id == "PORTIA104");
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
                                                           """, new ComponentPracticeGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA104");
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
                                                           """, new ComponentPracticeGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "PORTIA104");
    }
}
