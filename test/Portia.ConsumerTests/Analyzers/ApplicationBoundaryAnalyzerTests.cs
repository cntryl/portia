namespace Cntryl.Portia.Consumer;

public sealed class ApplicationBoundaryAnalyzerTests
{
    [Fact]
    public void ShouldRecognizeApplicationBoundaryGivenQualifiedAssemblyNames()
    {
        // Arrange
        var reference = GeneratorCompilation.ReferenceNamed("Coco.Shop.Api", "public class Dependency;");

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationBoundaryAnalyzer(),
            [("Features/Orders/Values/Price.cs", "public record Price;")],
            references: [reference], assemblyName: "Coco.Shop.Core");

        // Assert
        _ = Assert.Single(diagnostics, item => item.Id == "PORTIA206");
    }

    [Theory]
    [InlineData("Shop.Common", "Shop.Core", true)]
    [InlineData("Shop.Core", "Shop.Api", true)]
    [InlineData("Shop.Core", "Shop.Common", false)]
    [InlineData("Shop.Api", "Shop.Core", false)]
    [InlineData("Shop.Worker", "Shop.Core", false)]
    [InlineData("Shop.Core", "Cntryl.Portia.Cassie", false)]
    [InlineData("Shop.Common", "Cntryl.Portia.Cassie", true)]
    [InlineData("Shop.Common", "Cntryl.Portia.Fitz", true)]
    [InlineData("Shop.Common", "Portia.DependencyInjection", true)]
    [InlineData("Shop.Core", "Portia.Fitz", true)]
    [InlineData("Shop.Core", "Portia.Cassie", false)]
    [InlineData("Shop.Api", "Cntryl.Portia.Cassie", false)]
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
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationBoundaryAnalyzer(), [(path, source)]);

        // Assert
        Assert.Equal(forbidden ? 1 : 0, diagnostics.Length);
        Assert.All(diagnostics, item => Assert.Equal("PORTIA207", item.Id));
    }

    [Theory]
    [InlineData("Commands", "throw new System.NotImplementedException();", true)]
    [InlineData("Commands", "throw new System.InvalidOperationException();", false)]
    [InlineData("Queries", "throw new System.NotImplementedException();", false)]
    public void ShouldReportCommandStubGivenUnimplementedOperation(string requestFolder, string body, bool forbidden)
    {
        // Arrange
        var request = $"namespace Shop.Common.Features.Orders.{requestFolder}; public record PlaceOrder : Cntryl.Portia.IRequest;";
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
        var request = $"namespace Shop.Common.Features.Orders.{requestFolder}; public record FindOrder : Cntryl.Portia.IRequest;";
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
        Assert.All(diagnostics, item => Assert.Equal("PORTIA212", item.Id));
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
        const string source = "public static class Setup { public static void Configure() => Cntryl.Portia.CassiePortiaBuilderExtensions.AddCassie(); }";

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
            [("Repositories/OrderRepository.cs", "public class OrderRepository;")], references: [repositoryApi, client]);

        // Assert
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ShouldIgnoreMethodNameGivenUnrelatedHydrationApi()
    {
        // Arrange
        var reference = GeneratorCompilation.ReferenceNamed("Shop.Common", "namespace Shop.Common.Features.Orders.Queries; public record FindOrder : Cntryl.Portia.IRequest;");
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
        const string source = "public static class Setup { public static void Configure() => Cntryl.Portia.CassiePortiaBuilderExtensions.AddCassie(); }";

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationBoundaryAnalyzer(),
            [(filename, source)], enabled: enabled, references: [reference]);

        // Assert
        Assert.Empty(diagnostics);
    }
}
