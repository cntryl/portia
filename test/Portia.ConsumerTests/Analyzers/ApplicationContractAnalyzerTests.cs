namespace Cntryl.Portia.Consumer;

public sealed class ApplicationContractAnalyzerTests
{
    [Theory]
    [InlineData("public bool IsValid() => true;")]
    [InlineData("public int Total => 2 + 2;")]
    [InlineData("public int Total { get { return 4; } }")]
    [InlineData("public Price() { }")]
    [InlineData("public static Price operator +(Price left, Price right) => left;")]
    [InlineData("public System.Guid Id { get; init; } = System.Guid.NewGuid();")]
    public void ShouldReportBehaviorGivenExecutableCommonMember(string member)
    {
        // Arrange
        var source = $"namespace Shop.Common.Features.Orders.Values; public record Price {{ {member} }}";

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Common", new ApplicationContractAnalyzer(),
            [("Features/Orders/Values/Price.cs", source)]);

        // Assert
        _ = Assert.Single(diagnostics, item => item.Id == "PORTIA209");
    }

    [Theory]
    [InlineData("public record Price(decimal Amount);")]
    [InlineData("public class Price { public decimal Amount { get; init; } = 0; }")]
    [InlineData("public enum Currency { Usd, Cad }")]
    public void ShouldAllowDataGivenDeclarativeCommonType(string declaration)
    {
        // Arrange
        var source = "namespace Shop.Common.Features.Orders.Values; " + declaration;

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Common", new ApplicationContractAnalyzer(),
            [("Features/Orders/Values/Price.cs", source)]);

        // Assert
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void ShouldAllowValueConstructionGivenSealedRecordInValues()
    {
        const string source = """
                              namespace Shop.Common.Features.Orders.Values;
                              public sealed record Price
                              {
                                  public decimal Amount { get; }
                                  public Price(decimal amount) { if (amount < 0) throw new System.ArgumentOutOfRangeException(nameof(amount)); Amount = decimal.Round(amount, 2); }
                                  public decimal Total() => Amount;
                              }
                              """;
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Common", new ApplicationContractAnalyzer(),
            [("Features/Orders/Values/Price.cs", source)]);
        var behavior = Assert.Single(diagnostics, item => item.Id == "PORTIA209");
        Assert.Contains("Total", behavior.GetMessage(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("public record Price", "Features/Orders/Values/Price.cs")]
    [InlineData("public sealed record Price", "Features/Orders/Models/Price.cs")]
    [InlineData("public sealed class Price", "Features/Orders/Values/Price.cs")]
    public void ShouldReportConstructorOutsideSealedValueRecord(string declaration, string path)
    {
        var source = $"{declaration} {{ public Price() {{ }} }}";
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Common", new ApplicationContractAnalyzer(), [(path, source)]);
        _ = Assert.Single(diagnostics, item => item.Id == "PORTIA209");
    }

    [Theory]
    [InlineData("", "Commands", "IRequest")]
    [InlineData("[Discriminator(\"orders.place\")]", "Commands", "IRequest")]
    [InlineData("[Discriminator(\"orders.place\", 0)]", "Commands", "IRequest")]
    [InlineData("[Discriminator(\"orders.PlaceOrder\", 1)]", "Commands", "IRequest")]
    [InlineData("[Discriminator(\"orders.place.order\", 1)]", "Commands", "IRequest")]
    [InlineData("[Discriminator(\"other.place\", 1)]", "Commands", "IRequest")]
    [InlineData("[Discriminator(\"orders.place\\n\", 1)]", "Commands", "IRequest")]
    [InlineData("[Discriminator(\"orders.place__order\", 1)]", "Commands", "IRequest")]
    [InlineData("", "Queries", "IStreamRequest<int>")]
    [InlineData("", "Events", "DomainEvent")]
    public void ShouldReportWireIdentityGivenMissingOrInvalidDiscriminator(string attribute, string folder, string contract)
    {
        // Arrange
        var source = $"using Cntryl.Portia; namespace Shop.Common.Features.Orders.{folder}; {attribute} public record PlaceOrder : {contract};";

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Common", new ApplicationContractAnalyzer(),
            [($"Features/Orders/{folder}/PlaceOrder.cs", source)]);

        // Assert
        _ = Assert.Single(diagnostics, item => item.Id == "PORTIA210");
    }

    [Theory]
    [InlineData("[Discriminator(\"orders.place_order\", 1)]", "Orders")]
    [InlineData("[Discriminator(name: \"orders.place_order\", version: 2)]", "Orders")]
    [InlineData("[Discriminator(version: 2, name: \"order_lines.place_order\")]", "OrderLines")]
    [InlineData("[Discriminator(\"vat_reports.request_report\", 1)]", "VATReports")]
    public void ShouldAllowWireIdentityGivenExplicitFeatureAndVersion(string attribute, string feature)
    {
        // Arrange
        var source = $"using Cntryl.Portia; namespace Shop.Common.Features.{feature}.Commands; {attribute} public record PlaceOrder : IRequest;";

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Common", new ApplicationContractAnalyzer(),
            [($"Features/{feature}/Commands/PlaceOrder.cs", source)]);

        // Assert
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData(false, "Price.cs")]
    [InlineData(true, "Price.g.cs")]
    public void ShouldSkipContractPolicyGivenDisabledOrGeneratedSource(bool enabled, string filename)
    {
        // Arrange
        const string source = "public record Price { public int Total => 4; }";

        // Act
        var diagnostics = ApplicationAnalyzerCompilation.Analyze("Shop.Common", new ApplicationContractAnalyzer(),
            [(filename, source)], enabled: enabled);

        // Assert
        Assert.Empty(diagnostics);
    }
}
