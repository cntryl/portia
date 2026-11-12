using System.Globalization;

namespace Cntryl.Portia.Consumer;

public sealed class RequestGuardGeneratorTests
{
    [Fact]
    public async Task ShouldEmitTypedDescriptorsAndDispatchGivenValidFamilyGuardRegistration()
    {
        // Arrange
        var assembly = GeneratorCompilation.Compile("""
                                                    using System.Collections.Generic;
                                                    using System.Threading;
                                                    using System.Threading.Tasks;
                                                    using Cntryl.Portia;
                                                    using Microsoft.Extensions.DependencyInjection;
                                                    public interface IFamily : IRequestBase;
                                                    public sealed record First : IRequest, IFamily;
                                                    public sealed record Second : IRequest<int>, IFamily;
                                                    public sealed class Handler : IRequestHandler<First>, IRequestHandler<Second, int>
                                                    {
                                                        public static readonly List<string> Calls = new();
                                                        public ValueTask<Result> HandleAsync(IRequestContext<First> c, CancellationToken ct) { Calls.Add("first-handler"); return ValueTask.FromResult(Result.Success); }
                                                        public ValueTask<Result<int>> HandleAsync(IRequestContext<Second> c, CancellationToken ct) { Calls.Add("second-handler"); return ValueTask.FromResult(Result<int>.Success(8)); }
                                                    }
                                                    public sealed class Guard : IRequestGuard<IFamily>
                                                    {
                                                        public ValueTask<Result> GuardAsync(IRequestContext<IFamily> c, CancellationToken ct) { Handler.Calls.Add("guard"); return ValueTask.FromResult(Result.Success); }
                                                    }
                                                    public static class Scenario
                                                    {
                                                        public static async Task<int> Run()
                                                        {
                                                            var services = new ServiceCollection();
                                                            services.AddPortia().AddRequestHandler<Handler>().AddRequestGuard<Guard>();
                                                            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
                                                            await using var scope = provider.CreateAsyncScope();
                                                            var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
                                                            await bus.SendAsync(new First(), RequestActor.System);
                                                            var second = await bus.SendAsync(new Second(), RequestActor.System);
                                                            return second.Value + Handler.Calls.Count;
                                                        }
                                                    }
                                                    """, new RegistrationCallInterceptorGenerator());

        // Act
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<Task<int>>>();

        // Assert
        Assert.Equal(12, await run());
    }

    [Fact]
    public void ShouldReportPortia018GivenNonGuardRegistration()
    {
        // Arrange
        const string source = """
                                                                        using Cntryl.Portia;
                                                                        using Microsoft.Extensions.DependencyInjection;
                                                                        public sealed class NotAGuard;
                                                                        public static class Scenario
                                                                        {
                                                                            public static void Register() => new ServiceCollection().AddPortia().AddRequestGuard<NotAGuard>();
                                                                        }
                                                                        """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source,
            new RegistrationCallInterceptorGenerator());

        // Assert
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA018");
        Assert.Contains("does not implement a Portia guard interface",
            diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRemainCompilationErrorGivenMalformedGuardSignature()
    {
        // Arrange
        const string source = """
                                                                 using System.Threading;
                                                                 using System.Threading.Tasks;
                                                                 using Cntryl.Portia;
                                                                 using Microsoft.Extensions.DependencyInjection;
                                                                 public sealed record Command : IRequest;
                                                                 public sealed class Broken : IRequestGuard<Command>
                                                                 {
                                                                     public ValueTask<Result> GuardAsync(IRequestContext<Command> context) => ValueTask.FromResult(Result.Success);
                                                                 }
                                                                 public static class Scenario
                                                                 {
                                                                     public static void Register() => new ServiceCollection().AddPortia().AddRequestGuard<Broken>();
                                                                 }
                                                                 """;

        // Act
        var diagnostics = GeneratorCompilation.OutputDiagnostics(source, new RegistrationCallInterceptorGenerator());

        // Assert
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "CS0535");
    }

    [Fact]
    public void ShouldReportPortia015GivenGenericGuardDeclaration()
    {
        // Arrange
        const string source = """
                                                                        using System.Threading;
                                                                        using System.Threading.Tasks;
                                                                        using Cntryl.Portia;
                                                                        public sealed record Command : IRequest;
                                                                        public sealed class Guard<T> : IRequestGuard<Command>
                                                                        {
                                                                            public ValueTask<Result> GuardAsync(IRequestContext<Command> context, CancellationToken ct) => ValueTask.FromResult(Result.Success);
                                                                        }
                                                                        """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new RequestShapeDiagnosticsGenerator());

        // Assert
        var diagnostic = Assert.Single(diagnostics,
            item => item.Id == "PORTIA015");
        Assert.Contains("generic component types are unsupported",
            diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShouldEmitDescriptorAndRunBeforeHandlerGivenStreamGuardRegistration()
    {
        // Arrange
        var assembly = GeneratorCompilation.Compile("""
                                                    using System.Collections.Generic;
                                                    using System.Runtime.CompilerServices;
                                                    using System.Threading;
                                                    using System.Threading.Tasks;
                                                    using Cntryl.Portia;
                                                    using Microsoft.Extensions.DependencyInjection;
                                                    public sealed record Feed : IStreamRequest<int>;
                                                    public sealed class Handler : IStreamRequestHandler<Feed, int>
                                                    {
                                                        public static readonly List<string> Calls = new();
                                                        public async IAsyncEnumerable<int> HandleAsync(IRequestContext<Feed> c, [EnumeratorCancellation] CancellationToken ct) { Calls.Add("handler"); yield return 1; await Task.CompletedTask; }
                                                    }
                                                    public sealed class Guard : IRequestGuard<Feed>
                                                    {
                                                        public ValueTask<Result> GuardAsync(IRequestContext<Feed> c, CancellationToken ct) { Handler.Calls.Add("guard"); return ValueTask.FromResult(Result.Success); }
                                                    }
                                                    public sealed class StreamInterfaceGuard : IRequestGuard<IStreamRequest<int>>
                                                    {
                                                        public ValueTask<Result> GuardAsync(IRequestContext<IStreamRequest<int>> c, CancellationToken ct) { Handler.Calls.Add("interface-guard"); return ValueTask.FromResult(Result.Success); }
                                                    }
                                                    public static class Scenario
                                                    {
                                                        public static async Task<string> Run()
                                                        {
                                                            var services = new ServiceCollection();
                                                            services.AddPortia().AddRequestHandler<Handler>().AddRequestGuard<Guard>().AddRequestGuard<StreamInterfaceGuard>();
                                                            await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
                                                            await using var scope = provider.CreateAsyncScope();
                                                            var bus = scope.ServiceProvider.GetRequiredService<IRequestBus>();
                                                            await foreach (var _ in bus.StreamAsync(new Feed(), RequestActor.System)) { }
                                                            return string.Join(",", Handler.Calls);
                                                        }
                                                    }
                                                    """, new RegistrationCallInterceptorGenerator());

        // Act
        var run = assembly.GetType("Scenario")!.GetMethod("Run")!.CreateDelegate<Func<Task<string>>>();

        // Assert
        Assert.Equal("guard,interface-guard,handler", await run());
    }

    [Fact]
    public void ShouldNameRejectedTypeGivenPortia018()
    {
        // Arrange
        const string source = """
                                                                        using Cntryl.Portia;
                                                                        using Microsoft.Extensions.DependencyInjection;
                                                                        public sealed class NotAGuard;
                                                                        public static class Scenario
                                                                        {
                                                                            public static void Register() => new ServiceCollection().AddPortia().AddRequestGuard<NotAGuard>();
                                                                        }
                                                                        """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source,
            new RegistrationCallInterceptorGenerator());

        // Assert
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA018");
        Assert.Equal(
            "Cannot register 'NotAGuard' as a Portia guard: the type does not implement a Portia guard interface",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ShouldReportPortia018GivenGenericGuardRegistration()
    {
        // Arrange
        const string source = """
                                                                        using Cntryl.Portia;
                                                                        public static class Scenario
                                                                        {
                                                                            public static void Register<TGuard>(PortiaBuilder builder) where TGuard : class => builder.AddRequestGuard<TGuard>();
                                                                        }
                                                                        """;

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source,
                new RegistrationCallInterceptorGenerator());

        // Assert
        var diagnostic = Assert.Single(diagnostics,
            item => item.Id == "PORTIA018");
        Assert.Contains("use a concrete named type",
            diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldHandleRegistrationCollisionGivenGuardNames()
    {
        // Arrange
        const string source = """
                                                             using System.Threading;
                                                             using System.Threading.Tasks;
                                                             using Cntryl.Portia;
                                                             namespace Requests
                                                             {
                                                                 [Discriminator("requests.component")]
                                                                 [RequestRoute("app", "components", "*", "run")]
                                                                 public sealed record Component : IRequest, IQueuable;
                                                             }
                                                             namespace Guards
                                                             {
                                                                 public sealed class Component : IRequestGuard<Requests.Component>
                                                                 {
                                                                     public ValueTask<Result> GuardAsync(IRequestContext<Requests.Component> context, CancellationToken ct) => ValueTask.FromResult(Result.Success);
                                                                 }
                                                             }
                                                             """;

        // Act
        var generated = GeneratorCompilation.GeneratedSource(source, new PortiaServiceRegistrationGenerator());

        // Assert
        Assert.Contains("AddRequestsComponent", generated, StringComparison.Ordinal);
    }
}
