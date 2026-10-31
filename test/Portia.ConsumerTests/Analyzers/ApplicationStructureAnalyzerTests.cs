namespace Cntryl.Portia.Consumer;

public sealed class ApplicationStructureAnalyzerTests
{
    [Fact]
    public void ShouldRejectRootHelperGivenJsonMarkerWithoutContextContract()
    {
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [("Helper.cs", "using Cntryl.Portia; namespace Shop.Core; [PortiaJsonContext] public class Helper;")]);
        Assert.Contains(diagnostics, item => item.Id == "PORTIA203");
    }

    [Fact]
    public void ShouldAcceptProjectionRepositoryGivenExistingPersistenceContract()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            namespace Shop.Core.Features.Orders.Repositories;
            public sealed class OrderRepository : IProjectionStore
            {
                public ValueTask<ProjectionCheckpoint> LoadCheckpointAsync(CheckpointIdentity identity, CancellationToken ct = default) => default;
                public ValueTask<IProjectionBatch> BeginAsync(ProjectionBatchContext context, CancellationToken ct = default) => default;
            }
            """;
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Repositories/OrderRepository.cs", source)]);
        Assert.DoesNotContain(diagnostics, item => item.Id == "PORTIA204");
    }

    [Fact]
    public void ShouldExplainModelsGivenCommonEnumInValues()
    {
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Common", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Values/Status.cs", "namespace Shop.Common.Features.Orders.Values; public enum Status { Pending, Complete }")]);
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA204");
        Assert.Contains("Models", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Common")]
    [InlineData("Core")]
    public void ShouldAcceptAssemblyJsonContextGivenRootCompositionFile(string role)
    {
        var source = $$"""
            #nullable enable
            using System;
            using System.Text.Json;
            using System.Text.Json.Serialization;
            using System.Text.Json.Serialization.Metadata;
            using Cntryl.Portia;
            namespace Shop.{{role}};
            [PortiaJsonContext]
            public sealed class ContractsJsonContext(JsonSerializerOptions options) : JsonSerializerContext(options)
            {
                protected override JsonSerializerOptions? GeneratedSerializerOptions => null;
                public override JsonTypeInfo? GetTypeInfo(Type type) => null;
            }
            """;
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop." + role, new ApplicationStructureAnalyzer(),
            [("ContractsJsonContext.cs", source)]);
        Assert.Empty(diagnostics);
    }

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
    [InlineData("PageOfT", "public record PageOfT<T>;", "")]
    [InlineData("PairOfT2", "public record Pair<T, U>;", "")]
    [InlineData("Page.Details", "public record Page;", "PORTIA201")]
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

    [Fact]
    public void ShouldAcceptAspectFilenamesGivenActualPartialDeclarations()
    {
        const string declaration = "namespace Shop.Core.Features.Orders.Values; public partial record Page<T>;";
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Values/PageOfT.cs", declaration),
             ("Features/Orders/Values/PageOfT.Serialization.cs", declaration)]);
        Assert.DoesNotContain(diagnostics, item => item.Id == "PORTIA201");
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
    [InlineData("Features/Orders/Helpers/Nested/Price.cs")]
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

    [Theory]
    [InlineData("Capabilities", "IAcceptOrderHandler", "public interface IAcceptOrderHandler : Cntryl.Portia.IRequestHandler<Cntryl.Portia.IRequest>;")]
    [InlineData("Execution", "OrderCoordinator", "public sealed class OrderCoordinator { public void Run() { } }")]
    [InlineData("Sources", "OrderSource", "public sealed class OrderSource;")]
    [InlineData("Actions", "OrderAction", "public enum OrderAction { Create, Cancel }")]
    [InlineData("Actions", "OrderActions", "public static class OrderActions { public const string Create = \"create\"; }")]
    [InlineData("Actions", "OrderOperations", "public static class OrderOperations { public const string Create = \"create\"; }")]
    [InlineData("Errors", "OrderErrors", "public static class OrderErrors;")]
    [InlineData("Errors", "OrderErrorCodes", "public static class OrderErrorCodes;")]
    [InlineData("Models", "OrderView", "public sealed record OrderView(string Number);")]
    [InlineData("Events", "OrderPlaced", "public sealed record OrderPlaced : Cntryl.Portia.DomainEvent;")]
    public void ShouldAcceptExtendedCoreRoleGivenMatchingTypeShape(string folder, string name, string declaration)
    {
        var source = $"namespace Shop.Core.Features.Orders.{folder}; {declaration}";
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [($"Features/Orders/{folder}/{name}.cs", source)]);
        Assert.DoesNotContain(diagnostics, item => item.Id is "PORTIA203" or "PORTIA204");
    }

    [Theory]
    [InlineData("IOrderGuard", "public interface IOrderGuard : Cntryl.Portia.IRequestGuard<Cntryl.Portia.IRequest>;")]
    [InlineData("IOrderBehavior", "public interface IOrderBehavior : Cntryl.Portia.IRequestPipelineBehavior<Cntryl.Portia.IRequest>;")]
    public void ShouldAcceptRequestExecutionComponentGivenExecutionFolder(string name, string declaration)
    {
        var source = $"namespace Shop.Core.Features.Orders.Execution; {declaration}";
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [($"Features/Orders/Execution/{name}.cs", source)]);
        Assert.DoesNotContain(diagnostics, item => item.Id is "PORTIA203" or "PORTIA204");
    }

    [Fact]
    public void ShouldAllowNestedRoleAreasGivenNestedHandler()
    {
        const string source = "namespace Shop.Core.Features.Orders.Capabilities.Commands; public interface IAcceptOrderHandler : Cntryl.Portia.IRequestHandler<Cntryl.Portia.IRequest>;";
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Capabilities/Commands/AcceptOrderHandler.cs", source)]);
        Assert.DoesNotContain(diagnostics, item => item.Id is "PORTIA203" or "PORTIA204");
    }

    [Theory]
    [InlineData("Events", "EventCatalog")]
    [InlineData("Execution", "HostExecutor")]
    [InlineData("Hosting", "WorkerHost")]
    public void ShouldAllowSharedCoreFolderGivenSupportType(string folder, string name)
    {
        var source = $"namespace Shop.Core.{folder}; public sealed class {name};";
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [($"{folder}/{name}.cs", source)]);
        Assert.DoesNotContain(diagnostics, item => item.Id is "PORTIA203" or "PORTIA204");
    }

    [Fact]
    public void ShouldAllowFeatureRegistrationGivenTopLevelFeatureCompositionFile()
    {
        const string source = "namespace Shop.Core.Features; public static class OrdersPortiaExtensions;";
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [("Features/OrdersPortiaExtensions.cs", source)]);
        Assert.DoesNotContain(diagnostics, item => item.Id == "PORTIA203");
    }

    [Theory]
    [InlineData("Features/Orders/OrderJsonContext.cs", "Shop.Core.Features.Orders")]
    [InlineData("Features/Orders/Serialization/OrderJsonContext.cs", "Shop.Core.Features.Orders.Serialization")]
    public void ShouldAllowFeatureJsonContextGivenSupportedCompositionLocation(string path, string @namespace)
    {
        var source = $$"""
            #nullable enable
            using System;
            using System.Text.Json;
            using System.Text.Json.Serialization;
            using System.Text.Json.Serialization.Metadata;
            using Cntryl.Portia;
            namespace {{@namespace}};
            [PortiaJsonContext]
            public sealed class OrderJsonContext(JsonSerializerOptions options) : JsonSerializerContext(options)
            {
                protected override JsonSerializerOptions? GeneratedSerializerOptions => null;
                public override JsonTypeInfo? GetTypeInfo(Type type) => null;
            }
            """;
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [(path, source)]);
        Assert.DoesNotContain(diagnostics, item => item.Id is "PORTIA203" or "PORTIA204");
    }

    [Fact]
    public void ShouldIgnoreNestedTypesGivenTopLevelOwner()
    {
        const string source = "namespace Shop.Core.Tests; public class Price { private class Fixture; public enum Kind { One } }";
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core.Tests", new ApplicationStructureAnalyzer(),
            [("Price.cs", source)], isTest: true);
        Assert.DoesNotContain(diagnostics, item => item.Id is "PORTIA200" or "PORTIA201");
    }

    [Fact]
    public void ShouldRequireAggregateRootGivenAggregateFolder()
    {
        const string source = "namespace Shop.Core.Features.Orders.Aggregates; public sealed class CancellationRule { public bool Allows(bool shipped) => !shipped; }";
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Aggregates/CancellationRule.cs", source)]);
        _ = Assert.Single(diagnostics, item => item.Id == "PORTIA204");
    }
    [Fact]
    public void ShouldAllowDirectlyReferencedCollaboratorGivenAggregateFolder()
    {
        const string collaborator = "namespace Shop.Core.Features.Orders.Aggregates; public sealed class CancellationRule { public bool Allows(bool shipped) => !shipped; }";
        const string aggregate = """
                                 using Cntryl.Portia;
                                 namespace Shop.Core.Features.Orders.Aggregates;
                                 public sealed class Order(Uuid id, CancellationRule rule) : Aggregate(id, new EventStreamAddress("shop", "orders", id.ToString()));
                                 """;
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Aggregates/CancellationRule.cs", collaborator), ("Features/Orders/Aggregates/Order.cs", aggregate)]);
        Assert.DoesNotContain(diagnostics, item => item.Id is "PORTIA203" or "PORTIA204");
    }

    [Fact]
    public void ShouldRequireDirectlyReferencedCollaboratorGivenAggregateFolder()
    {
        const string collaborator = "namespace Shop.Core.Features.Orders.Execution; public sealed class CancellationRule { public bool Allows(bool shipped) => !shipped; }";
        const string aggregate = """
                                 using Cntryl.Portia;
                                 using Shop.Core.Features.Orders.Execution;
                                 namespace Shop.Core.Features.Orders.Aggregates;
                                 public sealed class Order(Uuid id, CancellationRule rule) : Aggregate(id, new EventStreamAddress("shop", "orders", id.ToString()));
                                 """;
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Execution/CancellationRule.cs", collaborator), ("Features/Orders/Aggregates/Order.cs", aggregate)]);
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA204");
        Assert.Contains("directly owned aggregate helpers belong with their root",
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldAllowUnownedBehaviorHelperGivenExecutionFolder()
    {
        const string source = "namespace Shop.Core.Features.Orders.Execution; public sealed class OrderCoordinator { public void Run() { } }";
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Execution/OrderCoordinator.cs", source)]);
        Assert.DoesNotContain(diagnostics, item => item.Id is "PORTIA203" or "PORTIA204");
    }

    [Fact]
    public void ShouldAllowDataOnlyModelGivenAutoProperties()
    {
        const string source = "namespace Shop.Common.Features.Orders.Models; public sealed record OrderView(string Number);";
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Common", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Models/OrderView.cs", source)]);
        Assert.DoesNotContain(diagnostics, item => item.Id == "PORTIA204");
    }

    [Fact]
    public void ShouldAllowSafeStringRepresentationGivenValueRecord()
    {
        // Arrange
        const string source = """
                              namespace Shop.Common.Features.Orders.Values;
                              public sealed record Secret
                              {
                                  public override string ToString() => "Secret { [REDACTED] }";
                              }
                              """;

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Common", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Values/Secret.cs", source)]);

        // Assert
        Assert.DoesNotContain(diagnostics, item => item.Id == "PORTIA204");
    }

    [Fact]
    public void ShouldAllowEnumModelGivenConstantDataShape()
    {
        const string source = "namespace Shop.Common.Features.Orders.Models; public enum OrderState { New, Paid }";
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Common", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Models/OrderState.cs", source)]);
        Assert.DoesNotContain(diagnostics, item => item.Id == "PORTIA204");
    }

    [Theory]
    [InlineData("public sealed class OrderView;")]
    [InlineData("public sealed class OrderView { public static string Name { get; } }")]
    public void ShouldRequireModelDataEvidenceGivenModelFolder(string declaration)
    {
        var source = $"namespace Shop.Common.Features.Orders.Models; {declaration}";
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Common", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Models/OrderView.cs", source)]);
        _ = Assert.Single(diagnostics, item => item.Id == "PORTIA204");
    }

    [Theory]
    [InlineData("public sealed class OrderView { public string Number => \"1\"; }")]
    [InlineData("public sealed class OrderView { public void Normalize() { } }")]
    [InlineData("public sealed class OrderView { public event System.Action? Changed; }")]
    public void ShouldRejectExecutableModelShapeGivenModelFolder(string declaration)
    {
        var source = $"namespace Shop.Common.Features.Orders.Models; {declaration}";
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Common", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Models/OrderView.cs", source)]);
        _ = Assert.Single(diagnostics, item => item.Id == "PORTIA204");
    }

    [Fact]
    public void ShouldKeepOwnershipWithinFeatureGivenReferenceFromAnotherAggregate()
    {
        const string rule = "namespace Shop.Core.Features.Orders.Aggregates; public sealed class CancellationRule;";
        const string aggregate = """
                                 using Cntryl.Portia;
                                 using Shop.Core.Features.Orders.Aggregates;
                                 namespace Shop.Core.Features.Other.Aggregates;
                                 public sealed class Other(Uuid id, CancellationRule rule) : Aggregate(id, new EventStreamAddress("shop", "other", id.ToString()));
                                 """;
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Aggregates/CancellationRule.cs", rule), ("Features/Other/Aggregates/Other.cs", aggregate)]);
        var diagnostic = Assert.Single(diagnostics, item => item.Id == "PORTIA204");
        Assert.Contains("CancellationRule", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldRequireRepositoryContractInTheSameFeature()
    {
        const string contract = "namespace Shop.Core.Features.Orders.Ports; public interface IOrders;";
        const string repository = "using Shop.Core.Features.Orders.Ports; namespace Shop.Core.Features.Orders.Repositories; public sealed class Orders : IOrders;";
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Core", new ApplicationStructureAnalyzer(),
            [("Features/Orders/Ports/IOrders.cs", contract), ("Features/Orders/Repositories/Orders.cs", repository)]);
        Assert.DoesNotContain(diagnostics, item => item.Id == "PORTIA204");
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
    [InlineData("Repositories", "public interface IOrders;", "IOrders")]
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
