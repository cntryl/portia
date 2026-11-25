namespace Cntryl.Portia.Consumer;

public sealed class ApplicationBoundaryAnalyzerTests
{
    [Fact]
    public void ShouldReportMissingRegistrationGivenInternalSystemRequestHandler()
    {
        // Arrange
        const string source = """
                              using Cntryl.Portia;
                              using System.Threading;
                              using System.Threading.Tasks;
                              namespace Shop.Core.Features.Orders.Handlers;
                              [Discriminator("orders.refresh", 1)]
                              public sealed record Refresh : IRequest, ISystemRequest;
                              public sealed class RefreshHandler : IRequestHandler<Refresh>
                              {
                                  public ValueTask<Result> HandleAsync(
                                      IRequestContext<Refresh> context, CancellationToken ct) => default;
                              }
                              """;

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core",
            new ApplicationBoundaryAnalyzer(), [("Features/Orders/Handlers/RefreshHandler.cs", source)]);

        // Assert
        Assert.Single(diagnostics, item => item.Id == "PORTIA214");
    }

    [Fact]
    public void ShouldAllowRegisteredInternalSystemRequestHandlerGivenFeatureRegistration()
    {
        // Arrange
        const string source = """
                              using Cntryl.Portia;
                              using System.Threading;
                              using System.Threading.Tasks;
                              namespace Shop.Core.Features.Orders;
                              [Discriminator("orders.refresh", 1)]
                              public sealed record Refresh : IRequest, ISystemRequest;
                              public sealed class RefreshHandler : IRequestHandler<Refresh>
                              {
                                  public ValueTask<Result> HandleAsync(
                                      IRequestContext<Refresh> context, CancellationToken ct) => default;
                              }
                              public static class OrdersPortiaExtensions
                              {
                                  public static void Add(PortiaBuilder builder) =>
                                      builder.AddRequestHandler<RefreshHandler>();
                              }
                              """;

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core",
            new ApplicationBoundaryAnalyzer(), [("Features/Orders/OrdersPortiaExtensions.cs", source)]);

        // Assert
        Assert.DoesNotContain(diagnostics, item => item.Id == "PORTIA214");
    }

    [Fact]
    public void ShouldRecognizeApplicationBoundaryGivenQualifiedAssemblyNames()
    {
        // Arrange
        var reference = GeneratorCompilation.ReferenceNamed("Example.Shop.Api", "public class Dependency;");

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationBoundaryAnalyzer(),
            [("Features/Orders/Values/Price.cs", "public record Price;")],
            references: [reference], assemblyName: "Example.Shop.Core");

        // Assert
        _ = Assert.Single(diagnostics, item => item.Id == "PORTIA206");
    }

    [Fact]
    public void ShouldAllowAbstractionsGivenQualifiedCoreAssemblyName()
    {
        // Arrange
        var reference = GeneratorCompilation.ReferenceNamed("Example.Shop.Abstractions", "public class Port;");

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationBoundaryAnalyzer(),
            [("Features/Orders/Values/Price.cs", "public record Price;")],
            references: [reference], assemblyName: "Example.Shop.Core");

        // Assert
        Assert.DoesNotContain(diagnostics, item => item.Id == "PORTIA206");
    }

    [Theory]
    [InlineData("Shop.Common", "Shop.Core", true)]
    [InlineData("Shop.Core", "Shop.Api", true)]
    [InlineData("Shop.Core", "Shop.Common", false)]
    [InlineData("Shop.Core", "Shop.Abstractions", false)]
    [InlineData("Shop.Core", "Shop.Billing.Abstractions", false)]
    [InlineData("Shop.Core", "Shop.AbstractionsHost", true)]
    [InlineData("Shop.Common", "Shop.Abstractions", true)]
    [InlineData("Shop.Api", "Shop.Core", false)]
    [InlineData("Shop.Worker", "Shop.Core", false)]
    [InlineData("Shop.Core", "Cntryl.Portia.Cassie", false)]
    [InlineData("Shop.Common", "Cntryl.Portia.Cassie", true)]
    [InlineData("Shop.Common", "Cntryl.Portia.Fitz", true)]
    [InlineData("Shop.Common", "Portia.DependencyInjection", true)]
    [InlineData("Shop.Core", "Portia.Fitz", true)]
    [InlineData("Shop.Core", "Portia.Cassie", false)]
    [InlineData("Shop.Core", "Portia.Communications.Abstractions", false)]
    [InlineData("Shop.Common", "Portia.Communications.Abstractions", false)]
    [InlineData("Shop.Common", "Portia.DataProtection.Abstractions", false)]
    [InlineData("Shop.Core", "Cntryl.Portia.DataProtection.Abstractions", false)]
    [InlineData("Shop.Core", "Portia.DataProtection", true)]
    [InlineData("Shop.Core", "Cntryl.Portia.DataProtection.Aws", true)]
    [InlineData("Shop.Api", "Portia.DataProtection.Aws", false)]
    [InlineData("Shop.Worker", "Portia.DataProtection", false)]
    [InlineData("Shop.Common", "Portia.Secrets.Abstractions", false)]
    [InlineData("Shop.Core", "Cntryl.Portia.Secrets.Abstractions", false)]
    [InlineData("Shop.Core", "Portia.Secrets.Aws", true)]
    [InlineData("Shop.Worker", "Portia.Secrets.Aws", false)]
    [InlineData("Shop.Core", "Portia.Storage.Azure", true)]
    [InlineData("Shop.Core", "Cntryl.Portia.Storage.Gcp", true)]
    [InlineData("Shop.Core", "Cntryl.Portia.Communications.Aws", true)]
    [InlineData("Shop.Core", "Portia.Communications.Azure", true)]
    [InlineData("Shop.Core", "Portia.Communications.Twilio", true)]
    [InlineData("Shop.Api", "Cntryl.Portia.Cassie", false)]
    [InlineData("Shop.Api", "Shop.Worker", true)]
    [InlineData("Shop.Worker", "Shop.Api", true)]
    [InlineData("Shop.Worker", "Shop.Common", false)]
    public void ShouldEnforceBoundaryGivenProjectReference(string project, string dependency, bool forbidden)
    {
        // Arrange
        var reference = GeneratorCompilation.ReferenceNamed(dependency, "public class Dependency;");

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze(project, new ApplicationBoundaryAnalyzer(),
            [("Values/Price.cs", $"namespace {project}.Values; public record Price;")], references: [reference]);

        // Assert
        Assert.Equal(forbidden ? 1 : 0, diagnostics.Length);
        Assert.All(diagnostics, item => Assert.Equal("PORTIA206", item.Id));
    }

    [Theory]
    [InlineData("Shop.Api", "Shop.Worker", "Shop.Api")]
    [InlineData("Shop.Worker", "Shop.Api", "Shop.Worker")]
    [InlineData("Shop.Api", "Example.Shop.Worker", "Example.Shop.Api")]
    public void ShouldRejectSiblingHostReferenceGivenApplicationPrefix(
        string project,
        string dependency,
        string assemblyName
    )
    {
        // Arrange
        var reference = GeneratorCompilation.ReferenceNamed(dependency, "public class Dependency;");

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze(project, new ApplicationBoundaryAnalyzer(),
            [("Values/Dependency.cs", "public class Consumer;")], references: [reference], assemblyName: assemblyName);

        // Assert
        _ = Assert.Single(diagnostics, item => item.Id == "PORTIA206");
    }

    [Theory]
    [InlineData("Features/Orders/Handlers/OrderRegistration.cs", true)]
    [InlineData("Features/Orders/OrdersPortiaExtensions.cs", false)]
    public void ShouldEnforceSingleRegistrationFileGivenCoreRegistration(string path, bool forbidden)
    {
        // Arrange
        const string source = """
                              using Cntryl.Portia;
                              using System.Threading;
                              using System.Threading.Tasks;
                              namespace Shop.Core.Features.Orders;
                              public static class OrdersPortiaExtensions
                              {
                                  public static PortiaBuilder AddOrders(this PortiaBuilder builder) => builder.AddRequestHandler<Handler>();
                              }
                              public class Handler : IRequestHandler<IRequest>
                              {
                                  public ValueTask<Result> HandleAsync(IRequestContext<IRequest> context, CancellationToken ct) => ValueTask.FromResult(Result.Success);
                              }
                              """;

        // Act
        var diagnostics =
            ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationBoundaryAnalyzer(), [(path, source)]);

        // Assert
        Assert.Equal(forbidden ? 1 : 0, diagnostics.Length);
        Assert.All(diagnostics, item => Assert.Equal("PORTIA207", item.Id));
    }

    [Fact]
    public void ShouldGroupOnlyUnsuppressedRegistrationsGivenScopedPragma()
    {
        // Arrange
        const string source = """
                              using Cntryl.Portia;
                              namespace Shop.Core.Features.Orders;
                              public static class Setup
                              {
                                  public static void Add(PortiaBuilder builder)
                                  {
                              #pragma warning disable PORTIA207
                                      builder.AddRequestHandler<Handler>();
                              #pragma warning restore PORTIA207
                                      builder.AddRequestHandler<Handler>();
                                      builder.AddRequestHandler<Handler>();
                                  }
                              }
                              public class Handler : IRequestHandler<IRequest>
                              {
                                  public System.Threading.Tasks.ValueTask<Result> HandleAsync(IRequestContext<IRequest> context, System.Threading.CancellationToken ct) => System.Threading.Tasks.ValueTask.FromResult(Result.Success);
                              }
                              """;

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationBoundaryAnalyzer(),
            [("Features/Orders/Handlers/Setup.cs", source)]);

        // Assert
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA207");
        Assert.Contains("2 feature", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
        Assert.Single(diagnostic.AdditionalLocations);
        Assert.True(diagnostic.Location.SourceSpan.Start < diagnostic.AdditionalLocations[0].SourceSpan.Start);
    }

    [Theory]
    [InlineData("Commands", "throw new System.NotImplementedException();", true)]
    [InlineData("Commands", "throw new System.InvalidOperationException();", false)]
    [InlineData("Queries", "throw new System.NotImplementedException();", false)]
    public void ShouldReportCommandStubGivenUnimplementedOperation(string requestFolder, string body, bool forbidden)
    {
        // Arrange
        var request =
            $"namespace Shop.Common.Features.Orders.{requestFolder}; public record PlaceOrder : Cntryl.Portia.IRequest;";
        var handler = $$"""
                        using Cntryl.Portia;
                        using System.Threading;
                        using System.Threading.Tasks;
                        using Shop.Common.Features.Orders.{{requestFolder}};
                        namespace Shop.Core.Features.Orders.Handlers;
                        public class PlaceOrderHandler : IRequestHandler<PlaceOrder>
                        {
                            public ValueTask<Result> HandleAsync(IRequestContext<PlaceOrder> context, CancellationToken ct) { {{body}} }
                        }
                        """;

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationBoundaryAnalyzer(),
            [("Features/Orders/Handlers/PlaceOrderHandler.cs", handler)],
            references: [GeneratorCompilation.ReferenceNamed("Shop.Common", request)]);

        // Assert
        Assert.Equal(forbidden ? 1 : 0, diagnostics.Length);
        Assert.All(diagnostics, item => Assert.Equal("PORTIA211", item.Id));
    }

    [Theory]
    [InlineData("Queries", true)]
    [InlineData("Commands", false)]
    public void ShouldRestrictHydrationGivenRequestRole(string requestFolder, bool forbidden)
    {
        // Arrange
        var request =
            $"namespace Shop.Common.Features.Orders.{requestFolder}; public record FindOrder : Cntryl.Portia.IRequest;";
        var handler = $$"""
                        using Cntryl.Portia;
                        using System.Threading;
                        using System.Threading.Tasks;
                        using Shop.Common.Features.Orders.{{requestFolder}};
                        namespace Shop.Core.Features.Orders.Handlers;
                        public class FindOrderHandler(IAggregateReader reader) : IRequestHandler<FindOrder>
                        {
                            public async ValueTask<Result> HandleAsync(IRequestContext<FindOrder> context, CancellationToken ct)
                            {
                                await reader.HydrateAsync<Aggregate>(null, ct);
                                return Result.Success;
                            }
                        }
                        """;

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationBoundaryAnalyzer(),
            [("Features/Orders/Handlers/FindOrderHandler.cs", handler)],
            references: [GeneratorCompilation.ReferenceNamed("Shop.Common", request)]);

        // Assert
        Assert.Equal(forbidden ? 1 : 0, diagnostics.Length);

        Assert.All(diagnostics, diagnostic =>
        {
            Assert.Equal("PORTIA212", diagnostic.Id);
            var message = diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);

            Assert.Contains("advisory", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("command handlers", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("authoritative outcomes", message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void ShouldReportHydrationGivenReactor()
    {
        // Arrange
        const string source = """
                              using Cntryl.Portia;
                              using System.Threading;
                              using System.Threading.Tasks;
                              namespace Shop.Core.Features.Orders.Reactors;
                              public class OrderReactor(IAggregateReader reader) : IReactorHandler<DomainEvent>
                              {
                                  public async ValueTask<Result> HandleAsync(IReactorContext<DomainEvent> context, CancellationToken ct)
                                  {
                                      await reader.HydrateAsync<Aggregate>(null, ct);
                                      return Result.Success;
                                  }
                              }
                              """;

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationBoundaryAnalyzer(),
            [("Features/Orders/Reactors/OrderReactor.cs", source)]);

        // Assert
        _ = Assert.Single(diagnostics, item => item.Id == "PORTIA212");
    }

    [Fact]
    public void ShouldReportIndirectHydrationGivenQueryCallsSameCompilationRepository()
    {
        // Arrange
        const string request = "namespace Shop.Common.Features.Orders.Queries; public record FindOrder : Cntryl.Portia.IRequest;";
        const string source = """
                            using Cntryl.Portia;
                            using System.Threading;
                            using System.Threading.Tasks;
                            using Shop.Common.Features.Orders.Queries;
                            public class FindOrderHandler(OrderRepository repository) : IRequestHandler<FindOrder>
                            {
                                public ValueTask<Result> HandleAsync(IRequestContext<FindOrder> context, CancellationToken ct)
                                    => repository.FindAsync(ct);
                            }
                            public class OrderRepository(IAggregateReader reader)
                            {
                                public async ValueTask<Result> FindAsync(CancellationToken ct)
                                {
                                    await reader.HydrateAsync<Aggregate>(null, ct);
                                    return Result.Success;
                                }
                            }
                            """;

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationBoundaryAnalyzer(),
            [("Features/Orders/Handlers/FindOrderHandler.cs", source)],
            references: [GeneratorCompilation.ReferenceNamed("Shop.Common", request)]);

        // Assert
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA212");
        Assert.StartsWith("repository.FindAsync", source.Substring(diagnostic.Location.SourceSpan.Start),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldAllowUnresolvedInterfaceRepositoryGivenQuery()
    {
        // Arrange
        var request = GeneratorCompilation.ReferenceNamed("Shop.Common",
            "namespace Shop.Common.Features.Orders.Queries; public record FindOrder : Cntryl.Portia.IRequest;");
        const string source = """
                            using Cntryl.Portia;
                            using System.Threading;
                            using System.Threading.Tasks;
                            using Shop.Common.Features.Orders.Queries;
                            public interface IOrderRepository { ValueTask<Result> FindAsync(CancellationToken ct); }
                            public class FindOrderHandler(IOrderRepository repository) : IRequestHandler<FindOrder>
                            {
                                public ValueTask<Result> HandleAsync(IRequestContext<FindOrder> context, CancellationToken ct)
                                    => repository.FindAsync(ct);
                            }
                            """;

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationBoundaryAnalyzer(),
            [("Features/Orders/Handlers/FindOrderHandler.cs", source)], references: [request]);

        // Assert
        Assert.DoesNotContain(diagnostics, item => item.Id == "PORTIA212");
    }

    [Fact]
    public void ShouldAllowProjectionReadGivenSameCompilationRepository()
    {
        // Arrange
        var request = GeneratorCompilation.ReferenceNamed("Shop.Common",
            "namespace Shop.Common.Features.Orders.Queries; public record FindOrder : Cntryl.Portia.IRequest;");
        const string source = """
                            using Cntryl.Portia;
                            using System.Threading;
                            using System.Threading.Tasks;
                            using Shop.Common.Features.Orders.Queries;
                            public class FindOrderHandler(OrderRepository repository) : IRequestHandler<FindOrder>
                            {
                                public ValueTask<Result> HandleAsync(IRequestContext<FindOrder> context, CancellationToken ct)
                                    => repository.FindAsync(ct);
                            }
                            public class OrderRepository
                            {
                                public ValueTask<Result> FindAsync(CancellationToken ct) => ValueTask.FromResult(Result.Success);
                            }
                            """;

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationBoundaryAnalyzer(),
            [("Features/Orders/Handlers/FindOrderHandler.cs", source)], references: [request]);

        // Assert
        Assert.DoesNotContain(diagnostics, item => item.Id == "PORTIA212");
    }

    [Fact]
    public void ShouldKeepEachQueryCallsiteGivenRepeatedCallsThroughGenericCyclicHelpers()
    {
        // Arrange
        var request = GeneratorCompilation.ReferenceNamed("Shop.Common",
            "namespace Shop.Common.Features.Orders.Queries; public record FindOrder : Cntryl.Portia.IRequest;");
        var calls = string.Join("\n", Enumerable.Range(0, 256).Select(_ => "            Helpers.First<Aggregate>(reader, ct);"));
        var source = $$"""
                     using Cntryl.Portia;
                     using System.Threading;
                     using System.Threading.Tasks;
                     using Shop.Common.Features.Orders.Queries;
                     public class FindOrderBase(IAggregateReader reader) : IRequestHandler<FindOrder>
                     {
                         ValueTask<Result> IRequestHandler<FindOrder>.HandleAsync(IRequestContext<FindOrder> context, CancellationToken ct)
                         {
                     {{calls}}
                             return ValueTask.FromResult(Result.Success);
                         }
                     }
                     public sealed class FindOrderHandler(IAggregateReader reader) : FindOrderBase(reader) { }
                     public static class Helpers
                     {
                         public static void First<T>(IAggregateReader reader, CancellationToken ct) where T : Aggregate => Second<T>(reader, ct);
                         static void Second<T>(IAggregateReader reader, CancellationToken ct) where T : Aggregate
                         {
                             if (ct.CanBeCanceled) First<T>(reader, ct);
                             _ = reader.HydrateAsync<T>(default!, ct);
                         }
                     }
                     """;

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationBoundaryAnalyzer(),
            [("Features/Orders/Handlers/FindOrderHandler.cs", source)], references: [request]);

        // Assert
        var hydrationDiagnostics = diagnostics.Where(item => item.Id == "PORTIA212").ToArray();
        Assert.Equal(256, hydrationDiagnostics.Length);
        Assert.Equal(256, hydrationDiagnostics.Select(item => item.Location.SourceSpan).Distinct().Count());
        Assert.All(hydrationDiagnostics, diagnostic =>
            Assert.StartsWith("Helpers.First<Aggregate>", source.Substring(diagnostic.Location.SourceSpan.Start),
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Shop.Core", true)]
    [InlineData("Shop.Api", false)]
    [InlineData("Shop.Worker", false)]
    public void ShouldRestrictConnectionSetupGivenPortiaCassieApi(string project, bool forbidden)
    {
        // Arrange
        var reference = GeneratorCompilation.ReferenceNamed("Portia.Cassie", """
                                                                             namespace Cntryl.Portia;
                                                                             public static class CassiePortiaBuilderExtensions { public static void AddCassie() { } }
                                                                             """);
        const string source =
            "public static class Setup { public static void Configure() => Cntryl.Portia.CassiePortiaBuilderExtensions.AddCassie(); }";

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze(project, new ApplicationBoundaryAnalyzer(),
            [("Setup.cs", source)], references: [reference]);

        // Assert
        Assert.Equal(forbidden ? 1 : 0, diagnostics.Length);
        Assert.All(diagnostics, item => Assert.Equal("PORTIA206", item.Id));
    }

    [Fact]
    public void ShouldPreserveRepositoryApiGivenTransitiveCassieClient()
    {
        // Arrange
        var repositoryApi = GeneratorCompilation.ReferenceNamed("Portia.Cassie", "public class RepositoryApi;");
        var client = GeneratorCompilation.ReferenceNamed("Cntryl.Cassie", "public class CassieClient;");

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationBoundaryAnalyzer(),
            [("Repositories/OrderRepository.cs", "public class OrderRepository;")],
            references: [repositoryApi, client]);

        // Assert
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ShouldIgnoreMethodNameGivenUnrelatedHydrationApi()
    {
        // Arrange
        var reference = GeneratorCompilation.ReferenceNamed("Shop.Common",
            "namespace Shop.Common.Features.Orders.Queries; public record FindOrder : Cntryl.Portia.IRequest;");
        const string source = """
                              using Cntryl.Portia;
                              using System.Threading;
                              using System.Threading.Tasks;
                              using Shop.Common.Features.Orders.Queries;
                              public class FindOrderHandler : IRequestHandler<FindOrder>
                              {
                                  public ValueTask<Result> HandleAsync(IRequestContext<FindOrder> context, CancellationToken ct)
                                  {
                                      HydrateAsync();
                                      return ValueTask.FromResult(Result.Success);
                                  }
                                  static void HydrateAsync() { }
                              }
                              """;

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationBoundaryAnalyzer(),
            [("FindOrderHandler.cs", source)], references: [reference]);

        // Assert
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData(false, "Setup.cs")]
    [InlineData(true, "Setup.g.cs")]
    public void ShouldSkipSourceBoundaryPolicyGivenDisabledOrGeneratedSource(bool enabled, string filename)
    {
        // Arrange
        var reference = GeneratorCompilation.ReferenceNamed("Portia.Cassie", """
                                                                             namespace Cntryl.Portia;
                                                                             public static class CassiePortiaBuilderExtensions { public static void AddCassie() { } }
                                                                             """);
        const string source =
            "public static class Setup { public static void Configure() => Cntryl.Portia.CassiePortiaBuilderExtensions.AddCassie(); }";

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationBoundaryAnalyzer(),
            [(filename, source)], enabled: enabled, references: [reference]);

        // Assert
        Assert.Empty(diagnostics);
    }
}
