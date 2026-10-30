namespace Cntryl.Portia.Consumer;

public sealed class ApplicationStructureAnalyzerTests
{
    [Fact]
    public void ShouldReportAdditionalTypesGivenMultipleDeclarations()
    {
        // Arrange
        const string source = "namespace Shop.Core.Features.Orders.Values; public record Price; public record Currency;";

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Values/Price.cs", source)]);

        // Assert
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA200");
        Assert.Equal("Currency", diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan));
    }

    [Theory]
    [InlineData("class Price { public class Currency; }")]
    [InlineData("class Price; public delegate void Changed();")]
    [InlineData("class Price; public enum Currency { Usd }")]
    public void ShouldCountEveryTypeKindGivenAdditionalDeclaration(string declaration)
    {
        // Arrange
        var source = "namespace Shop.Core.Features.Orders.Values; public " + declaration;

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Values/Price.cs", source)]);

        // Assert
        _ = Assert.Single(diagnostics, item => item.Id == "PORTIA200");
    }

    [Theory]
    [InlineData("Price", "public record Price;", "")]
    [InlineData("PageOfT", "public record Page<T>;", "")]
    [InlineData("Page", "public record Page<T>;", "PORTIA201")]
    [InlineData("Prices", "public record Price;", "PORTIA201")]
    public void ShouldMatchFilenameGivenTypeAndArity(string filename, string declaration, string expected)
    {
        // Arrange
        var source = "namespace Shop.Core.Features.Orders.Values; " + declaration;

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [($"Features/Orders/Values/{filename}.cs", source)]);

        // Assert
        Assert.Equal(expected.Length == 0 ? [] : new[] { expected }, diagnostics.Select(item => item.Id));
    }

    [Theory]
    [InlineData("Shop.Api", "Routes/Orders.cs", "namespace Wrong; public class Orders;", "Shop.Api.Routes")]
    [InlineData("Shop.Worker", "Jobs/Poll.cs", "namespace Shop.Worker; public class Poll;", "Shop.Worker.Jobs")]
    [InlineData("Shop.Core.Tests", "Features/Orders/Aggregates/OrderTests.cs", "public class OrderTests;", "Shop.Core.Tests.Features.Orders.Aggregates")]
    public void ShouldReportExpectedNamespaceGivenFolderMismatch(string project, string path, string source, string expected)
    {
        // Arrange
        var analyzer = new ApplicationStructureAnalyzer();

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze(project, analyzer, [(path, source)], isTest: project.EndsWith("Tests", StringComparison.Ordinal));

        // Assert
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA202");
        Assert.Contains(expected, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Features/Orders/Helpers/Price.cs")]
    [InlineData("Features/Orders/Values/Nested/Price.cs")]
    [InlineData("Values/Price.cs")]
    public void ShouldRejectUnknownFoldersGivenClosedFeatureLayout(string path)
    {
        // Arrange
        const string source = "public record Price;";

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(), [(path, source)]);

        // Assert
        _ = Assert.Single(diagnostics, item => item.Id == "PORTIA203");
    }

    [Theory]
    [InlineData("Values", "public interface IAcceptOrderHandler : Cntryl.Portia.IRequestHandler<Cntryl.Portia.IRequest>;", "IAcceptOrderHandler")]
    [InlineData("Handlers", "public record Price;", "Price")]
    [InlineData("Ports", "public class PaymentGateway;", "PaymentGateway")]
    public void ShouldReportMisplacedTypeGivenKnownFolderRole(string folder, string declaration, string name)
    {
        // Arrange
        var source = $"namespace Shop.Core.Features.Orders.{folder}; {declaration}";

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [($"Features/Orders/{folder}/{name}.cs", source)]);

        // Assert
        _ = Assert.Single(diagnostics, item => item.Id == "PORTIA204");
    }

    [Fact]
    public void ShouldAllowCompositionGivenAggregateOwnedRule()
    {
        // Arrange
        const string source = "namespace Shop.Core.Features.Orders.Aggregates; public sealed class CancellationRule { public bool Allows(bool shipped) => !shipped; }";

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Aggregates/CancellationRule.cs", source)]);

        // Assert
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ShouldReportMultipleRequestsGivenCombinedHandler()
    {
        // Arrange
        const string source = """
                              using Cntryl.Portia;
                              namespace Shop.Core.Features.Orders.Handlers;
                              public interface IOrderHandler : IRequestHandler<IRequest>, IRequestHandler<IRequest<int>, int>;
                              """;

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Handlers/IOrderHandler.cs", source)]);

        // Assert
        _ = Assert.Single(diagnostics, item => item.Id == "PORTIA205");
    }

    [Theory]
    [InlineData("Shop.Persistence")]
    [InlineData("Shop.Persistence.Cassie")]
    public void ShouldRejectPersistenceProjectGivenPortiaOwnedStores(string project)
    {
        // Arrange
        var source = $"namespace {project}; public class Store;";

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze(project, new ApplicationStructureAnalyzer(), [("Store.cs", source)]);

        // Assert
        _ = Assert.Single(diagnostics, item => item.Id == "PORTIA213");
    }

    [Theory]
    [InlineData(false, "Price.cs")]
    [InlineData(true, "Price.g.cs")]
    public void ShouldSkipConventionsGivenDisabledOrGeneratedCode(bool enabled, string filename)
    {
        // Arrange
        const string source = "public record Price; public record Currency;";

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [(filename, source)], enabled: enabled);

        // Assert
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ShouldHonorRootNamespaceGivenWindowsPaths()
    {
        // Arrange
        const string source = "namespace Acme.Shop.Core.Features.Orders.Values; public record Price;";

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [("Features\\Orders\\Values\\Price.cs", source)], rootNamespace: "Acme.Shop.Core", projectDirectory: "C:\\workspace\\Shop.Core");

        // Assert
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ShouldRecommendCompositionGivenLargeAggregateFile()
    {
        // Arrange
        var source = """
                     using Cntryl.Portia;
                     namespace Shop.Core.Features.Orders.Aggregates;
                     public sealed class Order(Uuid id) : Aggregate(id, new EventStreamAddress("shop", "orders", id.ToString()));
                     """ + string.Concat(Enumerable.Repeat("\n// A domain rule", 400));

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Aggregates/Order.cs", source)]);

        // Assert
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA208");
        Assert.Contains("compose", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("partial", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShouldHonorSuppressionGivenPragma()
    {
        // Arrange
        const string source = """
                              #pragma warning disable PORTIA200
                              namespace Shop.Core.Features.Orders.Values;
                              public record Price;
                              public record Currency;
                              """;

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Values/Price.cs", source)]);

        // Assert
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ShouldSkipFolderInferenceGivenLinkedSourceOutsideProject()
    {
        // Arrange
        const string source = "namespace Shop.Core.Features.Orders.Values; public record Price;";

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [("/shared/Price.cs", source)]);

        // Assert
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("Aggregates", "public class CancellationRule;", "CancellationRule")]
    [InlineData("Repositories", "public interface IOrders;", "IOrders")]
    [InlineData("Repositories", "public class OrderRepository;", "OrderRepository")]
    [InlineData("Ports", "public interface IPaymentGateway;", "IPaymentGateway")]
    [InlineData("Values", "public readonly record struct Price(decimal Amount);", "Price")]
    public void ShouldAllowOwnedTypeGivenCorrectCoreFolder(string folder, string declaration, string name)
    {
        // Arrange
        var source = $"namespace Shop.Core.Features.Orders.{folder}; {declaration}";

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [($"Features/Orders/{folder}/{name}.cs", source)]);

        // Assert
        Assert.Empty(diagnostics);
    }
}
