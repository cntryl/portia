namespace Cntryl.Portia.Consumer;

public sealed class GeneratorDiagnosticsTests
{
    [Fact]
    public void GenericHandlerHasActionableDiagnostic()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
            using Cntryl.Portia;
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
            public sealed record OrderCreated : DomainEvent;
            """, new DomainEventCatalogGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA021");
    }

    [Fact]
    public void UnsafeRequestRouteSegmentHasActionableDiagnostic()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
            using Cntryl.Portia;
            [RequestRoute("tenant/escape", "orders", "order", "create")]
            [Discriminator("orders.create")]
            public sealed record CreateOrder : IRequest, IQueuable;
            """, new PortiaServiceRegistrationGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA024");
    }

}
