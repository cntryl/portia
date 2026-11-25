using Microsoft.CodeAnalysis;

namespace Cntryl.Portia.Consumer;

public sealed class ScenarioObservationAnalyzerTests
{
    [Fact]
    public void ShouldReportOnlyDiscardedScenarioResultsGivenAssignmentsAndBodies()
    {
        // Arrange
        const string source = """
                            using System;
                            using Cntryl.Portia;
                            using Cntryl.Portia.Testing;
                            public sealed record Request : IRequest;
                            public static class Tests
                            {
                                public static void Run(IServiceProvider services)
                                {
                                    RequestScenario.For(services).When(new Request()).ExpectDenied();
                                    object _ = new();
                                    _ = RequestScenario.For(services).When(new Request()).ExpectDenied();
                                    Action local = () => RequestScenario.For(services).When(new Request()).ExpectDenied();
                                    Func<object> value = () => RequestScenario.For(services).When(new Request()).ExpectDenied();
                                    local();
                                    _ = value;
                                }
                                public static object Value(IServiceProvider services) =>
                                    RequestScenario.For(services).When(new Request()).ExpectDenied();
                                public static void Discard(IServiceProvider services) =>
                                    RequestScenario.For(services).When(new Request()).ExpectDenied();
                                public static void DiscardAssignment(IServiceProvider services)
                                {
                                    _ = (RequestScenario.For(services).When(new Request()).ExpectHandled());
                                }
                            }
                            """;
        var compilationDiagnostics = GeneratorCompilation.WarningsAsErrorsDiagnostics(source);

        // Act
        var diagnostics = GeneratorCompilation.Diagnostics(source, new ScenarioObservationAnalyzer());

        // Assert
        var reportedLines = diagnostics.Where(diagnostic => diagnostic.Id == "PORTIA107")
            .Select(diagnostic => diagnostic.Location.GetLineSpan().StartLinePosition.Line)
            .Order()
            .ToArray();
        Assert.DoesNotContain(compilationDiagnostics,
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal([8, 11, 19, 22], reportedLines);
    }
}
