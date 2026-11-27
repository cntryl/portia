using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cntryl.Portia.CompilerBenchmarks;

sealed partial class BenchmarkCase
{
    static BenchmarkCase PrepareApplication(CaseSpec spec, string size, CancellationToken ct)
    {
        var fixture = ApplicationFixture.Create(size);
        var before = spec.Operation == "full" ? null : fixture.Build(ct: ct);
        var edited = spec.Operation is "full" or "analyze" ? fixture : fixture.Edit(spec.Operation);
        ApplicationBuild? current = null;
        Diagnostic[] diagnostics = [];
        return new BenchmarkCase(async () =>
        {
            current = spec.Operation == "analyze" ? before! : edited.Build(before, ct);
            diagnostics = spec.Operation is "full" or "analyze"
                ? await ApplicationFixture.AnalyzeAsync(current, ct).ConfigureAwait(false)
                : await AnalyzeDocumentAsync(current, spec.Operation, ct).ConfigureAwait(false);
        }, async () =>
        {
            var run = current ?? throw new InvalidOperationException("Application request did not complete.");
            var incomplete = spec.Operation == "incomplete";
            var allDiagnostics = await ApplicationFixture.AnalyzeAsync(run, ct).ConfigureAwait(false);

            if (allDiagnostics.Any(diagnostic => diagnostic.Id == "AD0001")
                || (!incomplete && allDiagnostics.Length != 0))
                throw new InvalidOperationException(string.Join("\n", allDiagnostics.Select(diagnostic => diagnostic.ToString())));

            if (incomplete && !diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
                throw new InvalidOperationException("Incomplete edit failed to produce its compiler error.");

            foreach (var project in run.Projects)
                CompilerHost.CheckRun(project.Driver, project.Input, compile: !incomplete);

            var sources = run.Sources();
            var clean = edited.Build(ct: ct).Sources();
            Verification.Changes(clean, sources, []);

            if (before is not null && spec.Operation is "unchanged" or "body" or "irrelevant-reference" or "incomplete")
                Verification.Changes(before.Sources(), sources, []);


            if (before is not null && spec.Operation is "metadata" or "reference"
                && before.Sources().OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .SequenceEqual(sources.OrderBy(pair => pair.Key, StringComparer.Ordinal)))
                throw new InvalidOperationException("Cross-project edit did not invalidate generated output.");

            return Snapshot(sources, diagnostics.Length,
                "valid Common/Core/Api/Worker graph; compilation references; STJ enabled; "
                + (spec.Operation is "full" or "analyze" ? "whole-graph diagnostics" : "changed-document syntax/semantic diagnostics")
                + "; full diagnostics and consumer emit verified outside timer; " + spec.Operation);
        });
    }

    internal static async Task<Diagnostic[]> AnalyzeDocumentAsync(ApplicationBuild build, string edit, CancellationToken ct)
    {
        var role = edit is "metadata" or "reference" ? "Common" : "Core";
        var project = build.Projects.Single(project => project.Role == role);
        var file = role == "Common" ? "Change0.cs" : "Change0Handler.cs";
        var tree = project.Output.SyntaxTrees.Single(tree => tree.FilePath.EndsWith("/" + file, StringComparison.Ordinal));
        var options = new CompilationWithAnalyzersOptions(new AnalyzerOptions([], new FixtureOptions(role)), null,
            concurrentAnalysis: true, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: false);
        var driver = project.Output.WithAnalyzers([.. CompilerHost.CreateAnalyzers()], options);
        var syntax = await driver.GetAnalyzerSyntaxDiagnosticsAsync(tree, ct).ConfigureAwait(false);
        var semantic = await driver.GetAnalyzerSemanticDiagnosticsAsync(project.Output.GetSemanticModel(tree), null, ct)
            .ConfigureAwait(false);
        return [.. syntax, .. semantic, .. project.Output.GetSemanticModel(tree).GetDiagnostics(cancellationToken: ct)
            .Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning)];
    }
}
