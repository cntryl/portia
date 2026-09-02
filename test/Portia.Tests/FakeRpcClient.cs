using Cntryl.Fitz.Abstractions.Domains.Rpc;

namespace Cntryl.Portia;

/// <summary>
/// Stands in for the real Fitz RPC broker: routes a caller's <see cref="CallAsync" /> directly to
/// whichever worker last registered against that exact route.
/// </summary>
sealed class FakeRpcClient : IRpcClient
{
    readonly Dictionary<string, Func<RpcRequest, IRpcResponseWriter, CancellationToken, ValueTask>> _workers = [];

    public async IAsyncEnumerable<RpcResponseFrame> CallAsync(
        string route,
        ReadOnlyMemory<byte> body,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_workers.TryGetValue(route, out var worker))
            throw new InvalidOperationException($"No worker is registered for route '{route}'.");

        var writer = new CapturingResponseWriter();
        await worker(new RpcRequest(route, body), writer, ct).ConfigureAwait(false);

        foreach (var frame in writer.Frames)
            yield return frame;
    }

    public Task<RpcWorkerRegistration> RegisterWorkerAsync(
        string pattern,
        Func<RpcRequest, IRpcResponseWriter, CancellationToken, ValueTask> handler,
        RpcWorkerOptions? options = null,
        CancellationToken ct = default)
    {
        _workers[pattern] = handler;
        return Task.FromResult(new RpcWorkerRegistration(pattern, unregisterCt =>
        {
            _ = unregisterCt;
            _ = _workers.Remove(pattern);
            return ValueTask.CompletedTask;
        }));
    }

    sealed class CapturingResponseWriter : IRpcResponseWriter
    {
        public List<RpcResponseFrame> Frames { get; } = [];

        public ValueTask SendAsync(ReadOnlyMemory<byte> body, bool isFinal, CancellationToken ct = default)
        {
            Frames.Add(new RpcResponseFrame(body, (ulong)Frames.Count));
            return ValueTask.CompletedTask;
        }
    }
}
