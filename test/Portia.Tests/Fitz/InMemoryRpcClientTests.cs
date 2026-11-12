using Cntryl.Portia.Testing;

namespace Cntryl.Portia;

/// <summary>
///     Covers <see cref="InMemoryRpcClient" /> matching calls to worker registrations the way the Fitz
///     broker does, so a wildcard-routed callable request round-trips in tests as it does in production.
/// </summary>
public sealed class InMemoryRpcClientTests
{
    /// <summary>Fitz 2 calls carry their budget and complete their cancellation result.</summary>
    [Fact]
    public async Task ShouldExposeCompletionAndBudgetThroughTheFitzInterface()
    {
#pragma warning disable CA1859 // Exercise the shipped Fitz interface as a consumer would.
        // Arrange
        IRpcClient rpc = new InMemoryRpcClient();
#pragma warning restore CA1859
        TimeSpan? receivedBudget = null;
        await using var worker = await rpc.RegisterWorkerAsync("rpc://test", (request, _, _) =>
        {
            receivedBudget = request.RemainingTime;
            return ValueTask.CompletedTask;
        });

        // Act
        var call = rpc.CallAsync("rpc://test", ReadOnlyMemory<byte>.Empty, TimeSpan.FromMinutes(1));

        // Assert
        Assert.False(call.Cancellation.IsCompleted);
        await foreach (var _ in call)
        {
        }
        Assert.NotNull(receivedBudget);
        Assert.InRange(receivedBudget.Value, TimeSpan.Zero, TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(1));
        Assert.Equal(RpcCancellationOutcome.NotRequested, await call.Cancellation);
    }

    /// <summary>The timeout overload cancels work that exceeds its budget.</summary>
    [Fact]
    public async Task ShouldCancelWorkerWhenTheCallBudgetExpires()
    {
#pragma warning disable CA1859 // Exercise the shipped Fitz interface as a consumer would.
        // Arrange
        IRpcClient rpc = new InMemoryRpcClient();
#pragma warning restore CA1859
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker = await rpc.RegisterWorkerAsync("rpc://test", async (_, _, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        });

        // Act
        var call = rpc.CallAsync("rpc://test", ReadOnlyMemory<byte>.Empty, TimeSpan.FromSeconds(1));

        // Assert
        var failure = Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in call)
            {
            }
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _ = await failure.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RpcCancellationOutcome.Forwarded, await call.Cancellation.WaitAsync(TimeSpan.FromSeconds(5)));
    }

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
        // Arrange
        var rpc = new InMemoryRpcClient();
        string? received = null;
        await using var worker = await rpc.RegisterWorkerAsync(pattern, (request, _, _) =>
        {
            received = request.Route;
            return ValueTask.CompletedTask;
        });

        // Act
        await foreach (var _ in rpc.CallAsync("rpc://tenant-a/orders/place/run", ReadOnlyMemory<byte>.Empty))
        {
        }

        // Assert
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
        // Arrange
        var rpc = new InMemoryRpcClient();

        // Act
        await using var worker = await rpc.RegisterWorkerAsync(pattern, (_, _, _) => ValueTask.CompletedTask);

        // Assert
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
        // Arrange
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

        // Act
        await foreach (var _ in rpc.CallAsync("rpc://tenant-a/orders/place/run", ReadOnlyMemory<byte>.Empty))
        {
        }

        // Assert
        Assert.Equal(1, calls);
    }

    /// <summary>
    ///     Verifies workers can register, unregister and be called concurrently, as a host's startup
    ///     registrations and a test's calls do.
    /// </summary>
    [Fact]
    public async Task ShouldRegisterAndCallConcurrently()
    {
        // Arrange
        var rpc = new InMemoryRpcClient();

        // Act
        var exception = await Record.ExceptionAsync(() => Parallel.ForAsync(0, 2000, async (index, ct) =>
        {
            var pattern = $"rpc://tenant/area/resource-{index}/run";
            var registration = await rpc.RegisterWorkerAsync(pattern, (_, _, _) => ValueTask.CompletedTask,
                ct: ct);
            await foreach (var _ in rpc.CallAsync($"rpc://tenant/area/resource-{index}/run",
                               ReadOnlyMemory<byte>.Empty, ct))
            {
            }

            await registration.DisposeAsync();
        }));

        // Assert
        Assert.Null(exception);
    }
}
