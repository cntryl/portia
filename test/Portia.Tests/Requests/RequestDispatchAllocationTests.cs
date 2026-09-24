using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
///     Guards the steady-state allocation of dispatch. Both probe requests are deliberately plain —
///     no authorizer, no declared permission, no pipeline behavior — so what is measured is the
///     dispatch path itself rather than anything a policy added to it.
/// </summary>
/// <remarks>
///     A listener on Portia's activity source makes every dispatch allocate an activity, so the
///     measurement runs in the serialized telemetry collection, where no other test runs beside it.
/// </remarks>
[Collection(TelemetryTestGroup.Name)]
public sealed class RequestDispatchAllocationTests
{
    const int Iterations = 20_000;

    // Measured on an optimized build. The result adds nothing: its async result value lives in a
    // state machine that stays on the stack while dispatch completes synchronously.
    const int BudgetBytes = 80;

    /// <summary>
    ///     Both dispatch shapes stay inside the steady-state budget measured on an optimized build.
    /// </summary>
    [OptimizedBuildFact]
    public void ShouldStayWithinMeasuredSteadyStateDispatchBudgets()
    {
        using var host = TestRequestBus.Create();
        var context = host.Bus.CreateContext(new ClaimsPrincipal(new ClaimsIdentity()));
        var action = new AllocationProbeAction();
        var query = new AllocationProbeQuery();

        var noResultBytes = Measure(() => host.Bus.DispatchAsync(action, context));
        var resultBytes = Measure(() => host.Bus.DispatchAsync(query, context));

        Assert.True(noResultBytes <= BudgetBytes,
            $"No-result dispatch allocated {noResultBytes} B/call against a {BudgetBytes} B/call budget.");
        Assert.True(resultBytes <= BudgetBytes,
            $"Result-bearing dispatch allocated {resultBytes} B/call against a {BudgetBytes} B/call budget.");
    }

    // The budget is read per thread, so the measurement stays on one. Both handlers complete
    // synchronously, so nothing here needs to await; a dispatch that did suspend would move the
    // reading to another thread and measure noise, which the guard below reports rather than hides.
    static double Measure<TResult>(Func<ValueTask<TResult>> dispatch)
    {
        for (var i = 0; i < Iterations; i++)
            Complete(dispatch());

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Iterations; i++)
            Complete(dispatch());
        return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Iterations;
    }

    static void Complete<TResult>(ValueTask<TResult> pending)
    {
        Assert.True(pending.IsCompletedSuccessfully, "The probe dispatch did not complete synchronously.");
        _ = pending.Result;
    }

    // Without optimization the compiler emits every async state machine as a class, so each async hop
    // on the dispatch path allocates even when it completes synchronously. That is the compiler's debug
    // codegen, not the JIT's: a Debug build compiled with -p:Optimize=true measures the budget, and an
    // optimized build run under DOTNET_JITMinOpts=1 does too. The budget describes the code applications
    // ship, so it is asserted only against an optimized Portia.Core.
    sealed class OptimizedBuildFactAttribute : FactAttribute
    {
        public OptimizedBuildFactAttribute()
        {
            if (typeof(RequestBus).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled
                is true)
            {
                Skip = "Portia.Core is compiled without optimization, whose class state machines allocate on "
                       + "every async hop; the dispatch budget is measured against an optimized build (-c Release).";
            }
        }
    }
}
