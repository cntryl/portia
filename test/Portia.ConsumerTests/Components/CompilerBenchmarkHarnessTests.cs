using Cntryl.Portia.CompilerBenchmarks;

namespace Cntryl.Portia.Consumer;

public sealed class CompilerBenchmarkHarnessTests
{
    [Fact]
    public void ShouldRejectDifferentWarmupGivenPairedWorkerReports()
    {
        // Arrange
        var sample = new Sample("probe/all/body", "minimal", 0, 1, 1, 0, 0, 0, 0,
            new Evidence("same", 1, 1, 0, "probe"));
        var baseline = new WorkerReport(Campaign.Format, sample.Case, sample.Size, 0, "none", 1, "fixture", "harness",
            "runtime", "roslyn", "os", "arch", new Dictionary<string, string>
            {
                ["harness-il"] = "same",
                ["fixture-references"] = "same",
                ["System.Text.Json.SourceGeneration"] = "same"
            }, [sample]);
        var candidate = baseline with { Warmup = 1, WarmupSize = "minimal", ProcessId = 2 };
        var pairs = new[]
        {
            new WorkerResult(0, "baseline", "base.dll", 1, baseline, null),
            new WorkerResult(0, "candidate", "candidate.dll", 1, candidate, null)
        };

        // Act
        var exception = Record.Exception(() => Campaign.Compare(pairs));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void ShouldActivateDiagnosticGeneratorsGivenInvalidComponentAndMetadata()
    {
        // Arrange
        Action verify = GeneratorChecks.Verify;

        // Act
        var exception = Record.Exception(verify);

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task ShouldCountWorkerThreadAllocationGivenAsynchronousOperation()
    {
        // Arrange
        using var operation = new BenchmarkCase(async () =>
        {
            var buffer = await Task.Run(() => new byte[1024 * 1024]);
            GC.KeepAlive(buffer);
        }, () => Task.FromResult(new Evidence("verified", 0, 0, 0, "allocation probe")));

        // Act
        var sample = await BenchmarkCase.MeasurePreparedAsync(operation, new CaseSpec("probe", "async"), "minimal", 0);

        // Assert
        Assert.True(sample.AllocatedBytes >= 1024 * 1024);
    }

    [Fact]
    public async Task ShouldRejectSecondExecutionGivenPreparedIndependentCase()
    {
        // Arrange
        using var operation = await BenchmarkCase.PrepareAsync(new CaseSpec("generators", "body"), "minimal", TestContext.Current.CancellationToken);
        await operation.ExecuteAsync();

        // Act
        var exception = await Record.ExceptionAsync(() => operation.ExecuteAsync().AsTask());

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Theory]
    [InlineData("json-create")]
    [InlineData("json-add")]
    [InlineData("partial")]
    [InlineData("reactor")]
    [InlineData("containers")]
    public async Task ShouldRouteRealDiagnosticsGivenProjectFixAll(string fix)
    {
        // Arrange
        var spec = new CaseSpec("fixes", "fix-all", fix);

        // Act
        var sample = await BenchmarkCase.MeasureAsync(spec, "minimal", 0, ct: TestContext.Current.CancellationToken);

        // Assert
        Assert.True(sample.Evidence.Files > 0);
    }

    [Fact]
    public void ShouldRejectInvalidObservationsGivenNonfiniteComparison()
    {
        // Arrange
        var pairs = new[] { (1.0, double.NaN) };

        // Act
        var exception = Record.Exception(() => Campaign.CompareMetric("test", "ms", pairs, 1));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void ShouldIdentifyRegressionGivenConsistentlySlowerWorkers()
    {
        // Arrange
        var pairs = Enumerable.Repeat((100.0, 125.0), 10).ToArray();

        // Act
        var comparison = Campaign.CompareMetric("test", "ms", pairs, 1);

        // Assert
        Assert.Equal("regression", comparison.Result);
        Assert.True(comparison.Lower > comparison.Allowance);
    }

    [Fact]
    public void ShouldCoverShippedComponentsGivenBenchmarkManifest()
    {
        // Arrange
        var components = CompilerHost.Generators.Concat(CompilerHost.Analyzers).Concat(CompilerHost.CodeFixes)
            .ToArray();

        // Act
        var exception = Record.Exception(CompilerHost.VerifyInventory);

        // Assert
        Assert.Null(exception);
        Assert.Equal(17, components.Length);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("metadata")]
    [InlineData("route")]
    public async Task ShouldVerifyIncrementalOutputsGivenIndependentEdit(string edit)
    {
        // Arrange
        var spec = new CaseSpec("generators", edit);

        // Act
        var sample = await BenchmarkCase.MeasureAsync(spec, "minimal", 0, ct: TestContext.Current.CancellationToken);

        // Assert
        Assert.True(sample.Evidence.Files > 0);
        Assert.NotEmpty(sample.Evidence.OutputHash);
    }

    [Theory]
    [InlineData("split")]
    [InlineData("rename")]
    [InlineData("namespace")]
    [InlineData("version")]
    [InlineData("json-create")]
    [InlineData("json-add")]
    [InlineData("partial")]
    [InlineData("reactor")]
    [InlineData("containers")]
    public async Task ShouldCompileCorrectedConsumerGivenCompleteCodeFix(string fix)
    {
        // Arrange
        var spec = new CaseSpec("fixes", "complete", fix);

        // Act
        var sample = await BenchmarkCase.MeasureAsync(spec, "minimal", 0, ct: TestContext.Current.CancellationToken);

        // Assert
        Assert.True(sample.Evidence.Files > 0);
    }

    [Fact]
    public void ShouldPreserveUnchangedSyntaxTreesGivenIncrementalEdit()
    {
        // Arrange
        var fixture = CompilerFixture.Create("minimal");
        var old = fixture.Compilation.SyntaxTrees.ToArray();

        // Act
        var changed = fixture.Edit("body").Compilation.SyntaxTrees.ToArray();

        // Assert
        Assert.Equal(1, old.Zip(changed).Count(pair => !ReferenceEquals(pair.First, pair.Second)));
    }

    [Fact]
    public void ShouldRejectSkippedGeneratorGivenIncompleteRun()
    {
        // Arrange
        var expected = new[] { "Registration", "Http" };

        // Act
        var exception = Record.Exception(() => Verification.Coverage(expected, ["Registration"]));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void ShouldRejectMissingActionGivenFastEmptyDiscovery()
    {
        // Arrange
        var expected = new[] { "Portia.MakeProcessorPartial" };

        // Act
        var exception = Record.Exception(() => Verification.Actions(expected, []));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void ShouldRejectStaleOutputGivenContractEdit()
    {
        // Arrange
        var before = new Dictionary<string, string> { ["metadata"] = "old" };

        // Act
        var exception = Record.Exception(() => Verification.Changes(before, before, ["metadata"]));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    [Fact]
    public void ShouldRejectUnrelatedOutputChangesGivenLocalEdit()
    {
        // Arrange
        var before = new Dictionary<string, string> { ["metadata"] = "old", ["binding"] = "stable" };
        var after = new Dictionary<string, string> { ["metadata"] = "new", ["binding"] = "changed" };

        // Act
        var exception = Record.Exception(() => Verification.Changes(before, after, ["metadata"]));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }
}
