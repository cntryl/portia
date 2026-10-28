using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Cntryl.Portia.Consumer;

public sealed class WorkloadScopeDiagnosticsTests
{
    const string Usings = """
                          using Cntryl.Portia;
                          using Microsoft.Extensions.DependencyInjection;

                          """;

    [Theory]
    [InlineData("WorkloadScope.PerTenant", "ForPattern(\"accounts\")", "ForPattern", "ForTenant")]
    [InlineData("WorkloadScope.Global", "ForTenant(\"accounts\")", "ForTenant", "ForPattern")]
    public void ReportsAProjectorWhoseScopeContradictsItsDeclaredPattern(string scope, string pattern,
        string declared, string expected)
    {
        var source = Usings + $$"""
                                public sealed class Ledger(IProjectionStore store) : Projector(store, EventStreamPattern.{{pattern}});
                                public static class Scenario
                                {
                                    public static void Register() => new ServiceCollection().AddPortia().AddProjector<Ledger>("ledger", {{scope}});
                                }
                                """;

        var diagnostic = Assert.Single(Diagnostics(source), item => item.Id == "PORTIA031");

        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(
            $"'Ledger' is registered as workload 'ledger' with {scope}, but declares EventStreamPattern.{declared}(...); use EventStreamPattern.{expected}(...) for this scope",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.EndsWith($"AddProjector<Ledger>(\"ledger\", {scope})",
            source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("WorkloadScope.PerTenant", "ForTenant(\"accounts\")")]
    [InlineData("WorkloadScope.Global", "ForPattern(\"accounts\")")]
    public void AcceptsAProjectorWhoseScopeMatchesItsDeclaredPattern(string scope, string pattern)
    {
        var source = Usings + $$"""
                                public sealed class Ledger(IProjectionStore store) : Projector(store, EventStreamPattern.{{pattern}});
                                public static class Scenario
                                {
                                    public static void Register() => new ServiceCollection().AddPortia().AddProjector<Ledger>("ledger", {{scope}});
                                }
                                """;

        Assert.DoesNotContain(Diagnostics(source), item => item.Id == "PORTIA031");
    }

    [Fact]
    public void ReportsAReactorRegisteredGloballyWithATenantTemplate()
    {
        var source = Usings + """
                              public sealed class Mailer(IProjectionCheckpointStore checkpoints) : Reactor(checkpoints, EventStreamPattern.ForTenant("accounts"));
                              public static class Scenario
                              {
                                  public static void Register() => new ServiceCollection().AddPortia().AddReactor<Mailer>("mailer", WorkloadScope.Global);
                              }
                              """;

        var diagnostic = Assert.Single(Diagnostics(source), item => item.Id == "PORTIA031");

        Assert.Contains("'Mailer' is registered as workload 'mailer' with WorkloadScope.Global",
            diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsThePatternThroughAnAbstractBaseDeclaredInTheSameCompilation()
    {
        var source = Usings + """
                              public abstract class TenantProjector(IProjectionStore store) : Projector(store, EventStreamPattern.ForTenant("accounts"));
                              public sealed class Ledger(IProjectionStore store) : TenantProjector(store);
                              public static class Scenario
                              {
                                  public static void Register() => new ServiceCollection().AddPortia().AddProjector<Ledger>("ledger", WorkloadScope.Global);
                              }
                              """;

        _ = Assert.Single(Diagnostics(source), item => item.Id == "PORTIA031");
    }

    [Fact]
    public void ReadsThePatternFromAnExplicitBaseInitializer()
    {
        var source = Usings + """
                              public sealed class Ledger : Projector
                              {
                                  public Ledger(IProjectionStore store) : base(store, EventStreamPattern.ForPattern("accounts")) { }
                              }
                              public static class Scenario
                              {
                                  public static void Register() => new ServiceCollection().AddPortia().AddProjector<Ledger>("ledger", WorkloadScope.PerTenant);
                              }
                              """;

        _ = Assert.Single(Diagnostics(source), item => item.Id == "PORTIA031");
    }

    // A pattern chosen by the caller is checked at startup, where its value is known.
    [Fact]
    public void StaysSilentWhenThePatternIsNotDeclaredInSource()
    {
        var source = Usings + """
                              public sealed class Ledger(IProjectionStore store, EventStreamPattern pattern) : Projector(store, pattern);
                              public static class Scenario
                              {
                                  public static void Register() => new ServiceCollection().AddPortia().AddProjector<Ledger>("ledger", WorkloadScope.PerTenant);
                              }
                              """;

        Assert.DoesNotContain(Diagnostics(source), item => item.Id == "PORTIA031");
    }

    static IReadOnlyList<Diagnostic> Diagnostics(string source) =>
        GeneratorCompilation.Diagnostics(source, new WorkloadScopeDiagnosticsGenerator());
}
