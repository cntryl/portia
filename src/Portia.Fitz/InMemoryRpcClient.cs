using Cntryl.Fitz.Abstractions.Domains.Rpc;

namespace Cntryl.Portia;

/// <summary>
/// Stands in for a real Fitz RPC broker for tests: routes a caller's <see cref="CallAsync" />
/// directly to whichever worker last registered against that exact route, in-process, with no
/// network hop. Shipped from <c>Portia.Fitz</c> (not <c>Portia.Testing</c>) since it depends on
/// <c>Cntryl.Fitz.Abstractions</c> — an app testing its own RPC-based code can reuse this exact
/// type instead of writing its own fake.
/// </summary>
public sealed class InMemoryRpcClient : IRpcClient
{
    readonly Dictionary<string, Func<RpcRequest, IRpcResponseWriter, CancellationToken, ValueTask>> _workers = [];

    /// <inheritdoc />
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

    /// <inheritdoc />
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
