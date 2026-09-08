namespace Cntryl.Portia.Consumer;

public sealed class GeneratorShapeTests
{
    [Fact]
    public void ShouldReportUnsupportedCallSiteGivenRegistrationMethodGroupWhenGenerating()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            public sealed record Request : IRequest;
            public sealed class Handler : IRequestHandler<Request>
            {
                public ValueTask<Result> HandleAsync(IRequestContext<Request> context, CancellationToken ct) =>
                    ValueTask.FromResult(Result.Success);
            }
            public static class Scenario
            {
                public static Func<PortiaBuilder> Registration(PortiaBuilder portia) =>
                    portia.AddRequestHandler<Handler>;
            }
            """, new RegistrationCallInterceptorGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA019");
    }

    [Fact]
    public void ShouldResolveInheritedPropertyGivenPermissionTokenWhenRegisteringHandler()
    {
        const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            public abstract record AccountRequest(string AccountId) : IRequest;
            [RequiresPermission("accounts:{AccountId}:read")]
            public sealed record ReadAccount(string Id, string Account) : AccountRequest(Account);
            public sealed class Handler : IRequestHandler<ReadAccount>
            {
                public ValueTask<Result> HandleAsync(IRequestContext<ReadAccount> context, CancellationToken ct) =>
                    ValueTask.FromResult(Result.Success);
            }
            public static class Scenario
            {
                public static void Register(IServiceCollection services) =>
                    services.AddPortia().AddRequestHandler<Handler>();
            }
            """;

        var diagnostics = GeneratorCompilation.Diagnostics(source, new RegistrationCallInterceptorGenerator());
        var generated = GeneratorCompilation.GeneratedSource(source, new RegistrationCallInterceptorGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "PORTIA011" or "PORTIA013");
        Assert.Contains("typed.AccountId", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldEmitFailClosedInterceptorGivenInvalidPermissionWhenDiagnosticIsSuppressed()
    {
        var assembly = GeneratorCompilation.CompileSuppressing("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            [RequiresPermission("accounts:{Missing}:read")]
            public sealed record ReadAccount(string AccountId) : IRequest;
            public sealed class Handler : IRequestHandler<ReadAccount>
            {
                public ValueTask<Result> HandleAsync(IRequestContext<ReadAccount> context, CancellationToken ct) =>
                    ValueTask.FromResult(Result.Success);
            }
            public static class Scenario
            {
                public static bool Run()
                {
                    try { new ServiceCollection().AddPortia().AddRequestHandler<Handler>(); return false; }
                    catch (InvalidOperationException error) { return error.Message.Contains("{Missing}"); }
                }
            }
            """, "PORTIA011", new RegistrationCallInterceptorGenerator());

        var run = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<bool>>();
        Assert.True(run());
    }

    [Fact]
    public void ShouldReportNullableTokenGivenInheritedClassPropertyWhenRegisteringHandler()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            public abstract class AccountRequest { public string? AccountId { get; init; } }
            [RequiresPermission("accounts:{AccountId}:read")]
            public sealed class ReadAccount : AccountRequest, IRequest;
            public sealed class Handler : IRequestHandler<ReadAccount>
            {
                public ValueTask<Result> HandleAsync(IRequestContext<ReadAccount> context, CancellationToken ct) =>
                    ValueTask.FromResult(Result.Success);
            }
            public static class Scenario
            {
                public static void Register(IServiceCollection services) =>
                    services.AddPortia().AddRequestHandler<Handler>();
            }
            """, new RegistrationCallInterceptorGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "PORTIA013");
    }

    [Fact]
    public void ShouldIgnoreInvalidPermissionGivenUnselectedHandlerWhenGeneratingRegistrations()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
            using Cntryl.Portia;
            [RequiresPermission("accounts:{Missing}:read")]
            public sealed record ReadAccount(string AccountId) : IRequest;
            public sealed class Handler : IRequestHandler<ReadAccount>
            {
                public ValueTask<Result> HandleAsync(IRequestContext<ReadAccount> context, CancellationToken ct) =>
                    ValueTask.FromResult(Result.Success);
            }
            """, new RegistrationCallInterceptorGenerator());

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "PORTIA011" or "PORTIA013");
    }

    [Theory]
    [InlineData("accounts:{Missing}:read", "PORTIA011")]
    [InlineData("accounts:{AccountId}:read", "PORTIA013")]
    public void ShouldReportInvalidPermissionGivenSelectedHandlerWhenGeneratingRegistrations(string permission, string diagnosticId)
    {
        var diagnostics = GeneratorCompilation.Diagnostics($$"""
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            [RequiresPermission("{{permission}}")]
            public sealed record ReadAccount(string? AccountId) : IRequest;
            public sealed class Handler : IRequestHandler<ReadAccount>
            {
                public ValueTask<Result> HandleAsync(IRequestContext<ReadAccount> context, CancellationToken ct) =>
                    ValueTask.FromResult(Result.Success);
            }
            public static class Scenario
            {
                public static void Register(IServiceCollection services) =>
                    services.AddPortia().AddRequestHandler<Handler>();
            }
            """, new RegistrationCallInterceptorGenerator());

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == diagnosticId);
    }

    [Fact]
    public void ShouldNotGenerateLegacyRegistrationHelperGivenDomainEventWhenGeneratingCatalog()
    {
        var generated = GeneratorCompilation.GeneratedSource("""
            using Cntryl.Portia;
            public sealed record AccountOpened : DomainEvent;
            """, new DomainEventCatalogGenerator());

        Assert.Contains("AddPortiaGeneratedDomainEvents", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("AddGeneratedEvents", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("PortiaGeneratedEventRegistrations", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRegisterOnlyUsedExternalEventsGivenReferencedEventAssemblyWhenAddingPortia()
    {
        var contracts = GeneratorCompilation.Reference("""
            using Cntryl.Portia;
            namespace Contracts;
            public sealed record UsedEvent(string Value) : DomainEvent;
            public sealed record UnrelatedEvent(string Value) : DomainEvent;
            """);
        var generated = GeneratorCompilation.GeneratedSource("""
            using Cntryl.Portia;
            using Contracts;
            using Microsoft.Extensions.DependencyInjection;
            public sealed class Projection : IProjectorHandler<UsedEvent>
            {
                public System.Threading.Tasks.ValueTask HandleAsync(
                    UsedEvent ev,
                    IProjectorContext context,
                    System.Threading.CancellationToken ct) => System.Threading.Tasks.ValueTask.CompletedTask;
            }
            public static class Scenario
            {
                public static void Register(IServiceCollection services) => services.AddPortia();
            }
            """, [contracts], new RegistrationCallInterceptorGenerator());

        Assert.Contains("AddEvent<global::Contracts.UsedEvent>", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("UnrelatedEvent", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldNotGenerateComponentNamedRegistrationMethodsGivenHandlerWhenGeneratorRuns()
    {
        var generated = GeneratorCompilation.GeneratedSource("""
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            public sealed record Request : IRequest;
            public sealed class Handler : IRequestHandler<Request>
            {
                public ValueTask<Result> HandleAsync(IRequestContext<Request> context, CancellationToken ct) =>
                    ValueTask.FromResult(Result.Success);
            }
            public static class Scenario
            {
                public static void Register(IServiceCollection services) =>
                    services.AddPortia().AddRequestHandler<Handler>();
            }
            """, new RegistrationCallInterceptorGenerator());

        Assert.Contains("AddGeneratedHandler", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHandler", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("PortiaGeneratedRequestRegistrations", generated, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EveryImplementedRequestInterfaceDispatchesAndPartialDeclarationsAreUnique(bool partial)
    {
        var extra = partial ? "public partial class Handler { }" : "";
        var assembly = GeneratorCompilation.Compile("""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Microsoft.Extensions.DependencyInjection;
            public sealed record First : IRequest<int>;
            public sealed record Second : IRequest<int>;
            public partial class Handler : IRequestHandler<First, int>, IRequestHandler<Second, int>
            {
                public ValueTask<Result<int>> HandleAsync(IRequestContext<First> c, CancellationToken ct) => ValueTask.FromResult(Result<int>.Success(1));
                public ValueTask<Result<int>> HandleAsync(IRequestContext<Second> c, CancellationToken ct) => ValueTask.FromResult(Result<int>.Success(2));
            }
            public class Authorizer : IRequestAuthorizer<First>, IRequestAuthorizer<Second>
            {
                public static int Calls;
                public ValueTask<Result> AuthorizeAsync(IRequestContext<First> c, System.Security.Claims.ClaimsPrincipal actor, CancellationToken ct = default) { Calls++; return ValueTask.FromResult(Result.Success); }
                public ValueTask<Result> AuthorizeAsync(IRequestContext<Second> c, System.Security.Claims.ClaimsPrincipal actor, CancellationToken ct = default) { Calls++; return ValueTask.FromResult(Result.Success); }
            }
            public static class Scenario
            {
                public static async Task<int> Run()
                {
                    var services = new ServiceCollection();
                    services.AddPortia()
                        .AddRequestHandler<Handler>()
                        .AddRequestAuthorizer<Authorizer>();
                    await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
                    await using var scope = provider.CreateAsyncScope();
                    var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
                    return (await bus.SendAsync(new First(), RequestActor.System)).Value + (await bus.SendAsync(new Second(), RequestActor.System)).Value + Authorizer.Calls;
                }
            }
            """ + extra, new RegistrationCallInterceptorGenerator());
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<Task<int>>>();
        Assert.Equal(5, await run());
    }
}
