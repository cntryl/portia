namespace Cntryl.Portia.CompilerBenchmarks;

sealed partial class BenchmarkCase
{
    static BenchmarkCase PrepareAccounts(CaseSpec spec, CancellationToken ct)
    {
        var fixture = AccountsFixture.Load(spec.Component);
        var initial = fixture.Compilation();
        var input = spec.Operation == "body" ? AccountsFixture.Edit(initial) : initial;
        var driver = fixture.Driver();
        if (spec.Operation == "body")
            driver = driver.RunGenerators(initial, ct);
        var current = driver;
        return new BenchmarkCase(() =>
        {
            current = current.RunGenerators(input, ct);
            return ValueTask.CompletedTask;
        }, () =>
        {
            CompilerHost.CheckRun(current, input);
            var sources = CompilerHost.Sources(current);
            var clean = fixture.Driver().RunGenerators(input, ct);
            Verification.Changes(CompilerHost.Sources(clean), sources, []);
            return Task.FromResult(Snapshot(sources, current.GetRunResult().Diagnostics.Length,
                "captured Accounts " + spec.Component + "; exact SDK compiler options; frozen sources/references/external generators; current Portia generators; consumer emit after timer; " + spec.Operation));
        });
    }
}
