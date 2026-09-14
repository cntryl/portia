using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>
///     Guards the steady-state allocation of dispatch. Both probe requests are deliberately plain —
///     no authorizer, no declared permission, no pipeline behavior — so what is measured is the
///     dispatch path itself rather than anything a policy added to it.
/// </summary>
public sealed class RequestDispatchAllocationTests
{
    const int Iterations = 20_000;

    /// <summary>
    ///     Both dispatch shapes stay inside measured steady-state budgets. A generic result carries
    ///     one additional async result value, so equality with the no-result state machine is not a
    ///     factual invariant; the measured .NET 10 delta is bounded instead.
    /// </summary>
    [Fact]
    public void ShouldStayWithinMeasuredSteadyStateDispatchBudgets()
    {
        using var host = TestRequestBus.Create();
        var context = host.Bus.CreateContext(new ClaimsPrincipal(new ClaimsIdentity()));
        var action = new AllocationProbeAction();
        var query = new AllocationProbeQuery();

        var noResultBytes = Measure(() => host.Bus.DispatchAsync(action, context));
        var resultBytes = Measure(() => host.Bus.DispatchAsync(query, context));

        Assert.True(noResultBytes <= 432,
            $"No-result dispatch allocated {noResultBytes} B/call against a 432 B/call budget.");
        Assert.True(resultBytes <= 496,
            $"Result-bearing dispatch allocated {resultBytes} B/call against a 496 B/call budget.");
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
}
