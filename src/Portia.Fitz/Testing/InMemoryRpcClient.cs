using System.Runtime.CompilerServices;

namespace Cntryl.Portia.Testing;

/// <summary>
///     Stands in for a real Fitz RPC broker for tests: routes a caller's <see cref="CallAsync" />
///     directly to a worker whose registration pattern matches the concrete route, in-process, with no
///     network hop. Patterns match as the broker matches them — <c>*</c> stands for exactly one whole
///     segment and <c>**</c> for zero or more — so a per-tenant request registered under
///     <c>rpc://*/…</c> receives its tenant's calls. Fitz gives overlapping registrations no
///     precedence; this double calls the one registered most recently. Registration, unregistration
///     and calls are safe to run concurrently. Shipped from <c>Portia.Fitz</c> (not
///     <c>Portia.Testing</c>) since it depends on <c>Cntryl.Fitz.Abstractions</c> — an app testing its
///     own RPC-based code can reuse this exact type instead of writing its own fake.
/// </summary>
public sealed class InMemoryRpcClient : IRpcClient
{
    readonly Lock _gate = new();
    readonly List<Worker> _workers = [];

    /// <inheritdoc />
    public async IAsyncEnumerable<RpcResponseFrame> CallAsync(
        string route,
        ReadOnlyMemory<byte> body,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Worker? worker;
        lock (_gate)
            worker = _workers.LastOrDefault(candidate => Matches(route, candidate.Pattern));
        if (worker is null)
        {
            throw new InvalidOperationException($"No worker is registered for route '{route}'.");
        }

        var writer = new CapturingResponseWriter();
        await worker.Handler(new RpcRequest(route, body), writer, ct).ConfigureAwait(false);

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
        var worker = new Worker(pattern, handler);
        lock (_gate)
            _workers.Add(worker);
        return Task.FromResult(new RpcWorkerRegistration(pattern, unregisterCt =>
        {
            _ = unregisterCt;
            lock (_gate)
                _ = _workers.Remove(worker);
            return ValueTask.CompletedTask;
        }));
    }

    // Fitz's registration-pattern match: the schemes are equal, then segments compare one to one,
    // with "*" matching any single segment and "**" any run of segments, backtracking as needed.
    static bool Matches(string route, string pattern)
    {
        var routeMarker = route.IndexOf("://", StringComparison.Ordinal);
        var patternMarker = pattern.IndexOf("://", StringComparison.Ordinal);
        if (routeMarker < 1 || patternMarker < 1 ||
            !route.AsSpan(0, routeMarker).SequenceEqual(pattern.AsSpan(0, patternMarker)))
        {
            return false;
        }

        var routeSegments = route[(routeMarker + 3)..].Split('/');
        var patternSegments = pattern[(patternMarker + 3)..].Split('/');
        var routeIndex = 0;
        var patternIndex = 0;
        var lastDoubleWildcard = -1;
        var lastDoubleMatch = 0;
        while (routeIndex < routeSegments.Length)
        {
            var segment = patternIndex < patternSegments.Length ? patternSegments[patternIndex] : null;
            if (segment == "*" || string.Equals(segment, routeSegments[routeIndex], StringComparison.Ordinal))
            {
                routeIndex++;
                patternIndex++;
                continue;
            }

            if (segment == "**")
            {
                lastDoubleWildcard = patternIndex++;
                lastDoubleMatch = routeIndex;
                continue;
            }

            if (lastDoubleWildcard < 0)
            {
                return false;
            }

            routeIndex = ++lastDoubleMatch;
            patternIndex = lastDoubleWildcard + 1;
        }

        while (patternIndex < patternSegments.Length && patternSegments[patternIndex] == "**")
            patternIndex++;
        return patternIndex == patternSegments.Length;
    }

    // A class, not a record: unregistering removes this registration, never an equal one.
    sealed class Worker(string pattern, Func<RpcRequest, IRpcResponseWriter, CancellationToken, ValueTask> handler)
    {
        public string Pattern => pattern;
        public Func<RpcRequest, IRpcResponseWriter, CancellationToken, ValueTask> Handler => handler;
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
