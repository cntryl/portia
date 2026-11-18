using Cntryl.Portia.CompilerBenchmarks;
using Microsoft.CodeAnalysis.CSharp;

namespace Cntryl.Portia.Consumer;

public sealed class ContractsOnlyRegistrationGeneratorTests
{
    [Fact]
    public void ShouldReportInvalidTransportGivenContractsOnlyAssembly()
    {
        // Arrange
        var common = ApplicationFixture.Create("minimal").Build().Projects.Single(project => project.Role == "Common").User;
        var syntax = CSharpSyntaxTree.ParseText("""
            using Cntryl.Portia;
            [Discriminator("", 1)][RequestRoute("app", "bench", "broken", "run")] public sealed record Broken : IRequest, ICallable;
            """, CompilerHost.ParseOptions);
        var input = common.RemoveAllSyntaxTrees().AddSyntaxTrees(syntax);
        var driver = CompilerHost.Driver(component: nameof(PortiaServiceRegistrationGenerator), withJson: false);

        // Act
        var result = driver.RunGenerators(input).GetRunResult();

        // Assert
        Assert.Null(input.GetTypeByMetadataName("Cntryl.Portia.PortiaBuilder"));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Id == "PORTIA020");
        Assert.Empty(result.GeneratedTrees);
    }
}
