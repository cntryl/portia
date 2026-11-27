using Cntryl.Portia.CompilerBenchmarks;
using Microsoft.CodeAnalysis;

namespace Cntryl.Portia.Consumer;

public sealed class RepresentativeApplicationBenchmarkTests
{
    [Theory]
    [InlineData("unchanged")]
    [InlineData("body")]
    [InlineData("metadata")]
    [InlineData("reference")]
    [InlineData("irrelevant-reference")]
    [InlineData("incomplete")]
    public async Task ShouldVerifyFreshApplicationGivenIndependentEdit(string edit)
    {
        // Arrange
        var spec = new CaseSpec("applications", edit);

        // Act
        var sample = await BenchmarkCase.MeasureAsync(spec, "minimal", 0, ct: TestContext.Current.CancellationToken);

        // Assert
        Assert.True(sample.Evidence.Files > 0);
        Assert.NotEmpty(sample.Evidence.OutputHash);
    }

    [Fact]
    public void ShouldChangeOnlyCoreDirectReferencesGivenIrrelevantAssemblyAdded()
    {
        // Arrange
        var fixture = ApplicationFixture.Create("minimal");
        var cancellationToken = TestContext.Current.CancellationToken;
        var before = fixture.Build(ct: cancellationToken);

        // Act
        var after = fixture.Edit("irrelevant-reference").Build(before, cancellationToken);

        // Assert
        foreach (var oldProject in before.Projects)
        {
            var newProject = after.Projects.Single(project => project.Role == oldProject.Role);
            var added = newProject.Input.References.OfType<PortableExecutableReference>()
                .Except(oldProject.Input.References.OfType<PortableExecutableReference>());

            if (newProject.Role == "Core")
                Assert.Equal("Bench.Irrelevant.Abstractions.dll", Assert.Single(added).Display);
            else
                Assert.Empty(added);
        }
    }

    [Fact]
    public async Task ShouldCompileWithoutDiagnosticsGivenRepresentativeApplication()
    {
        // Arrange
        var fixture = ApplicationFixture.Create("minimal");
        string[] roles = ["Common", "Core", "Api", "Worker"];

        // Act
        var build = fixture.Build(ct: TestContext.Current.CancellationToken);
        var diagnostics = await ApplicationFixture.AnalyzeAsync(build, TestContext.Current.CancellationToken);
        var emissions = build.Projects.Select(project =>
        {
            using var output = new MemoryStream();
            return project.Output.Emit(output);
        }).ToArray();

        // Assert
        Assert.Equal(roles, build.Projects.Select(project => project.Role));
        Assert.Empty(diagnostics);
        Assert.All(emissions, emission => Assert.True(emission.Success, string.Join("\n", emission.Diagnostics)));
    }
}
