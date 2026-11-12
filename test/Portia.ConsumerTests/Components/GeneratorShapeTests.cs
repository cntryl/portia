namespace Cntryl.Portia.Consumer;

public sealed class GeneratorShapeTests
{
    [Theory]
    [InlineData("publisher.EnqueueWithOptionsAsync(new Queued(), RequestRouteValues.None, null, new RequestQueueOptions(TtlHours: 1))")]
    [InlineData("publisher.EnqueueWithOptionsAsync(new Queued(), RequestRouteValues.None, null, RequestMetadata.Create(), new RequestQueueOptions(TtlHours: 1), default)")]
    [InlineData("publisher.EnqueueWithOptionsAsync(new Queued(), RequestRouteValues.None, null, parent, new RequestQueueOptions(TtlHours: 1))")]
    public void ShouldRegisterOutboundQueueRequestGivenOptionsPublication(string invocation)
    {
        // Arrange
        var source = $$"""
                       using Cntryl.Portia;
                       using Microsoft.Extensions.DependencyInjection;
                       [RequestRoute("app", "jobs", "requests", "run")]
                       [Discriminator("test.queued_options")]
                       public sealed record Queued : IRequest, IQueuable;
                       public static class Scenario
                       {
                           public static void Run(IServiceCollection services, IRequestQueuePublisher publisher,
                               IExecutionContext parent)
                           {
                               var builder = services.AddPortia();
                               _ = {{invocation}};
                           }
                       }
                       """;

        // Act
        var generated = GeneratorCompilation.GeneratedSource(source, new RegistrationCallInterceptorGenerator());

        // Assert
        Assert.Contains("test.queued_options", generated, StringComparison.Ordinal);
        Assert.Contains("RequestTransportId(\"queue\")", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldDiscoverAnExternalAnnotatedTransportMarker()
    {
        // Arrange
        const string source = """
                              using Cntryl.Portia;
                              using Microsoft.Extensions.DependencyInjection;
                              [RequestTransport("custom-adapter")]
                              public interface ICustomTransport;
                              [RequestRoute("app", "custom", "requests", "send")]
                              [Discriminator("consumer.custom.request")]
                              public sealed record CustomRequest : IRequest, ICustomTransport;
                              public sealed class Handler : IRequestHandler<CustomRequest>
                              {
                                  public ValueTask<Result> HandleAsync(IRequestContext<CustomRequest> context,
                                      CancellationToken ct) => ValueTask.FromResult(Result.Success);
                              }
                              public static class Scenario
                              {
                                  public static PortiaBuilder Register(IServiceCollection services) =>
                                      services.AddPortia().AddRequestHandler<Handler>();
                              }
                              """;

        // Act
        var generated = GeneratorCompilation.GeneratedSource(source, new RegistrationCallInterceptorGenerator());

        // Assert
        Assert.Contains("RequestTransportId(\"custom-adapter\")", generated, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "builder.AddRequestSchedule(new Scheduled(), new RequestScheduleSpec(\"0 0 * * *\"), RequestRouteValues.None, RequestActor.System)")]
    [InlineData(
        "scheduler.EnsureAsync(new Scheduled(), new RequestScheduleSpec(\"0 0 * * *\"), RequestRouteValues.None, RequestActor.System)")]
    public void ShouldRegisterOutboundSchedulableRequestGivenScheduleDeclarationOrEnsure(string invocation)
    {
        // Arrange
        var source = $$"""
                       using Cntryl.Portia;
                       using Microsoft.Extensions.DependencyInjection;
                       [RequestRoute("app", "jobs", "daily", "run")]
                       [Discriminator("test.scheduled")]
                       public sealed record Scheduled : IRequest, ISchedulable;
                       public static class Scenario
                       {
                           public static void Run(IServiceCollection services, IRequestScheduler scheduler)
                           {
                               var builder = services.AddPortia();
                               _ = {{invocation}};
                           }
                       }
                       """;

        // Act
        var generated = GeneratorCompilation.GeneratedSource(source, new RegistrationCallInterceptorGenerator());

        // Assert
        Assert.Contains("test.scheduled", generated, StringComparison.Ordinal);
        Assert.Contains("RequestTransportId(\"schedule\")", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldReportUnsupportedCallSiteGivenRegistrationMethodGroupWhenGenerating()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using System;
                                                           using System.Threading;
                                                           using System.Threading.Tasks;
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
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

        var diagnostic = Assert.Single(diagnostics, candidate => candidate.Id == "PORTIA019");
        Assert.Contains("ordinary call or feature-local extension method",
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.DoesNotContain("reflection", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldResolveInheritedPropertyGivenPermissionTokenWhenRegisteringHandler()
    {
        // Arrange
        const string source = """
                              using System;
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Cntryl.Portia;
                              using Cntryl.Portia.Testing;
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

        // Act
        var generated = GeneratorCompilation.GeneratedSource(source, new RegistrationCallInterceptorGenerator());

        // Assert
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "PORTIA011" or "PORTIA013");
        Assert.Contains("typed.AccountId", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldEmitFailClosedInterceptorGivenInvalidPermissionWhenDiagnosticIsSuppressed()
    {
        // Arrange
        var assembly = GeneratorCompilation.CompileSuppressing("""
                                                               using System;
                                                               using System.Threading;
                                                               using System.Threading.Tasks;
                                                               using Cntryl.Portia;
                                                               using Cntryl.Portia.Testing;
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
                                                               """, "PORTIA011",
            new RegistrationCallInterceptorGenerator());

        // Act
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<bool>>();

        // Assert
        Assert.True(run());
    }

    [Fact]
    public void ShouldReportNullableTokenGivenInheritedClassPropertyWhenRegisteringHandler()
    {
        var diagnostics = GeneratorCompilation.Diagnostics("""
                                                           using Cntryl.Portia;
                                                           using Cntryl.Portia.Testing;
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
                                                           using Cntryl.Portia.Testing;
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
    public void ShouldReportInvalidPermissionGivenSelectedHandlerWhenGeneratingRegistrations(string permission,
        string diagnosticId)
    {
        var diagnostics = GeneratorCompilation.Diagnostics($$"""
                                                             using Cntryl.Portia;
                                                             using Cntryl.Portia.Testing;
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
                                                             using Cntryl.Portia.Testing;
                                                             [Discriminator("AccountOpened")]
                                                             public sealed record AccountOpened : DomainEvent;
                                                             """, new DomainEventCatalogGenerator());

        Assert.Contains("AddPortiaGeneratedDomainEvents", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("AddGeneratedEvents", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("PortiaGeneratedEventRegistrations", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRegisterOnlyUsedExternalEventsGivenReferencedEventAssemblyWhenAddingPortia()
    {
        // Arrange
        var contracts = GeneratorCompilation.Reference("""
                                                       using Cntryl.Portia;
                                                       using Cntryl.Portia.Testing;
                                                       namespace Contracts;
                                                       [Discriminator("UsedEvent")]
                                                       public sealed record UsedEvent(string Value) : DomainEvent;
                                                       [Discriminator("UnrelatedEvent")]
                                                       public sealed record UnrelatedEvent(string Value) : DomainEvent;
                                                       """);

        // Act
        var generated = GeneratorCompilation.GeneratedSource("""
                                                             using Cntryl.Portia;
                                                             using Cntryl.Portia.Testing;
                                                             using Contracts;
                                                             using Microsoft.Extensions.DependencyInjection;
                                                             public sealed class Projection : IProjectorHandler<UsedEvent>
                                                             {
                                                                 public async System.Threading.Tasks.ValueTask<Result> HandleAsync(
                                                                     IProjectorContext<UsedEvent> context,
                                                                     System.Threading.CancellationToken ct) { await System.Threading.Tasks.ValueTask.CompletedTask.ConfigureAwait(false); return Result.Success; }
                                                             }
                                                             public static class Scenario
                                                             {
                                                                 public static void Register(IServiceCollection services) => services.AddPortia();
                                                             }
                                                             """, [contracts],
            new RegistrationCallInterceptorGenerator());

        // Assert
        Assert.Contains("AddGeneratedEvent<global::Contracts.UsedEvent>(1, \"UsedEvent\")", generated,
            StringComparison.Ordinal);
        Assert.DoesNotContain("UnrelatedEvent", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldExcludeOpenGenericDomainEventFromAddPortiaRegistration()
    {
        // Arrange
        const string source = """
                              using Cntryl.Portia;
                              using Cntryl.Portia.Testing;
                              using Microsoft.Extensions.DependencyInjection;
                              [Discriminator("generic.event")]
                              public sealed record GenericEvent<T>(T Value) : DomainEvent;
                              [Discriminator("closed.event")]
                              public sealed record ClosedEvent : DomainEvent;
                              public static class Scenario
                              {
                                  public static void Register(IServiceCollection services) => services.AddPortia();
                              }
                              """;

        _ = GeneratorCompilation.Compile(source, new RegistrationCallInterceptorGenerator());

        // Act
        var generated = GeneratorCompilation.GeneratedSource(source, new RegistrationCallInterceptorGenerator());

        // Assert
        Assert.Contains("AddGeneratedEvent<global::ClosedEvent>", generated, StringComparison.Ordinal);
        Assert.DoesNotContain("GenericEvent", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldNotGenerateComponentNamedRegistrationMethodsGivenHandlerWhenGeneratorRuns()
    {
        var generated = GeneratorCompilation.GeneratedSource("""
                                                             using System.Threading;
                                                             using System.Threading.Tasks;
                                                             using Cntryl.Portia;
                                                             using Cntryl.Portia.Testing;
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
        // Arrange
        var extra = partial ? "public partial class Handler { }" : "";
        var assembly = GeneratorCompilation.Compile("""
                                                    using System;
                                                    using System.Threading;
                                                    using System.Threading.Tasks;
                                                    using Cntryl.Portia;
                                                    using Cntryl.Portia.Testing;
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
                                                        public ValueTask<Result> AuthorizeAsync(IRequestContext<First> c, CancellationToken ct) { Calls++; return ValueTask.FromResult(Result.Success); }
                                                        public ValueTask<Result> AuthorizeAsync(IRequestContext<Second> c, CancellationToken ct) { Calls++; return ValueTask.FromResult(Result.Success); }
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

        // Act
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<Task<int>>>();

        // Assert
        Assert.Equal(5, await run());
    }
}
