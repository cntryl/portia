using Cntryl.Portia.Testing;

namespace Cntryl.Portia;

/// <summary>
///     Covers <see cref="InMemoryRpcClient" /> matching calls to worker registrations the way the Fitz
///     broker does, so a wildcard-routed callable request round-trips in tests as it does in production.
/// </summary>
public sealed class InMemoryRpcClientTests
{
    /// <summary>
    ///     Verifies a concrete call reaches a worker registered under a whole-segment wildcard pattern —
    ///     the pattern <see cref="FitzRpcRequestServer" /> registers for a per-tenant request while
    ///     <see cref="FitzRemoteRequestSender" /> calls that request's concrete tenant route.
    /// </summary>
    [Theory]
    [InlineData("rpc://*/orders/place/run")]
    [InlineData("rpc://tenant-a/**")]
    [InlineData("rpc://**/run")]
    [InlineData("rpc://tenant-a/orders/place/run")]
    public async Task ShouldRouteAConcreteCallToAMatchingPattern(string pattern)
    {
        var rpc = new InMemoryRpcClient();
        string? received = null;
        await using var worker = await rpc.RegisterWorkerAsync(pattern, (request, _, _) =>
        {
            received = request.Route;
            return ValueTask.CompletedTask;
        });

        await foreach (var _ in rpc.CallAsync("rpc://tenant-a/orders/place/run", ReadOnlyMemory<byte>.Empty))
        {
        }

        Assert.Equal("rpc://tenant-a/orders/place/run", received);
    }

    /// <summary>
    ///     Verifies a wildcard stands for exactly one whole segment, and nothing matches across schemes.
    /// </summary>
    [Theory]
    [InlineData("rpc://*/orders/place")]
    [InlineData("rpc://*/orders/place/run/extra")]
    [InlineData("rpc://tenant*/orders/place/run")]
    [InlineData("rpc://tenant-b/**")]
    [InlineData("queue://*/orders/place/run")]
    public async Task ShouldNotRouteACallToANonMatchingPattern(string pattern)
    {
        var rpc = new InMemoryRpcClient();
        await using var worker = await rpc.RegisterWorkerAsync(pattern, (_, _, _) => ValueTask.CompletedTask);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in rpc.CallAsync("rpc://tenant-a/orders/place/run", ReadOnlyMemory<byte>.Empty))
            {
            }
        });
    }

    /// <summary>
    ///     Verifies disposing a superseded registration leaves the worker that replaced it in place.
    /// </summary>
    [Fact]
    public async Task ShouldKeepTheReplacingWorkerWhenASupersededRegistrationIsDisposed()
    {
        var rpc = new InMemoryRpcClient();
        var calls = 0;
        var superseded = await rpc.RegisterWorkerAsync("rpc://tenant-a/orders/place/run",
            (_, _, _) => ValueTask.CompletedTask);
        await using var current = await rpc.RegisterWorkerAsync("rpc://tenant-a/orders/place/run", (_, _, _) =>
        {
            calls++;
            return ValueTask.CompletedTask;
        });

        await superseded.DisposeAsync();
        await foreach (var _ in rpc.CallAsync("rpc://tenant-a/orders/place/run", ReadOnlyMemory<byte>.Empty))
        {
        }

        Assert.Equal(1, calls);
    }

    /// <summary>
    ///     Verifies workers can register, unregister and be called concurrently, as a host's startup
    ///     registrations and a test's calls do.
    /// </summary>
    [Fact]
    public async Task ShouldRegisterAndCallConcurrently()
    {
        var rpc = new InMemoryRpcClient();
        await Parallel.ForAsync(0, 2000, async (index, ct) =>
        {
            var pattern = $"rpc://tenant/area/resource-{index}/run";
            var registration = await rpc.RegisterWorkerAsync(pattern, (_, _, _) => ValueTask.CompletedTask,
                ct: ct);
            await foreach (var _ in rpc.CallAsync($"rpc://tenant/area/resource-{index}/run",
                               ReadOnlyMemory<byte>.Empty, ct))
            {
            }

            await registration.DisposeAsync();
        });
    }
}
