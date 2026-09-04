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
    public void NonPartialModuleHasActionableDiagnostic()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
            using Cntryl.Portia;
            [PortiaModule] public class Module { }
            """, new PortiaModuleGenerator());
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA014");
    }
}
