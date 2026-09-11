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
    ///     A result-bearing request is the ordinary case and must not cost more per call than a
    ///     no-result one: both resolve a cached pipeline plan, and resolving a cached plan should
    ///     allocate nothing. Asserting the two shapes against each other rather than against a byte
    ///     count keeps the invariant meaningful on a runtime that sizes async machinery differently.
    /// </summary>
    [Fact]
    public void ShouldNotAllocateMoreForResultBearingDispatchThanForNoResultDispatch()
    {
        using var host = TestRequestBus.Create();
        var context = host.Bus.CreateContext(new ClaimsPrincipal(new ClaimsIdentity()));
        var action = new AllocationProbeAction();
        var query = new AllocationProbeQuery();

        var noResultBytes = Measure(() => host.Bus.DispatchAsync(action, context));
        var resultBytes = Measure(() => host.Bus.DispatchAsync(query, context));

        Assert.True(resultBytes <= noResultBytes,
            $"Result-bearing dispatch allocated {resultBytes} B/call against {noResultBytes} B/call for a no-result dispatch.");
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
