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
    [InlineData("incomplete")]
    public async Task ShouldVerifyFreshApplicationGivenIndependentEdit(string edit)
    {
        // Arrange
        var spec = new CaseSpec("applications", edit);

        // Act
        var sample = await BenchmarkCase.MeasureAsync(spec, "minimal", 0);

        // Assert
        Assert.True(sample.Evidence.Files > 0);
        Assert.NotEmpty(sample.Evidence.OutputHash);
    }

    [Fact]
    public async Task ShouldCompileWithoutDiagnosticsGivenRepresentativeApplication()
    {
        // Arrange
        var fixture = ApplicationFixture.Create("minimal");
        string[] roles = ["Common", "Core", "Api", "Worker"];

        // Act
        var build = fixture.Build();
        var diagnostics = await ApplicationFixture.AnalyzeAsync(build);
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
