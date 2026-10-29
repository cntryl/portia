using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Cntryl.Portia.Testing;

/// <summary>
///     Stands in for a real Fitz RPC broker for tests: routes a caller's
///     <see cref="CallAsync(string, ReadOnlyMemory{byte}, CancellationToken)" />
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

    // Match IRpcClient's two overloads, including their optional cancellation tokens.
#pragma warning disable RS0026
    /// <inheritdoc />
    public RpcCall CallAsync(string route, ReadOnlyMemory<byte> body, CancellationToken ct = default) =>
        CallAsync(route, body, null, ct);

    /// <inheritdoc />
    public RpcCall CallAsync(string route, ReadOnlyMemory<byte> body, TimeSpan? timeout, CancellationToken ct = default)
    {
        if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(timeout), "RPC timeout must be between zero and one day.");

        var cancellation = new TaskCompletionSource<RpcCancellationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        return new RpcCall(CallCoreAsync(route, body, timeout, cancellation, ct), cancellation.Task);
    }
#pragma warning restore RS0026

    async IAsyncEnumerable<RpcResponseFrame> CallCoreAsync(
        string route,
        ReadOnlyMemory<byte> body,
        TimeSpan? timeout,
        TaskCompletionSource<RpcCancellationOutcome> cancellation,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var budgetStartedAt = Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } budget)
        {
            if (budget == TimeSpan.Zero)
                deadline.Cancel();
            else
                deadline.CancelAfter(budget);
        }
        var callToken = deadline.Token;

        try
        {
            callToken.ThrowIfCancellationRequested();
            Worker? worker;
            lock (_gate)
                worker = _workers.LastOrDefault(candidate => Matches(route, candidate.Pattern));
            if (worker is null)
            {
                cancellation.TrySetResult(RpcCancellationOutcome.NotRequested);
                throw new InvalidOperationException($"No worker is registered for route '{route}'.");
            }

            callToken.ThrowIfCancellationRequested();
            using var invocation = new WorkerInvocation(worker, route, body.ToArray(), timeout,
                budgetStartedAt, cancellation, callToken);
            await foreach (var frame in invocation.ReadAsync(callToken).ConfigureAwait(false))
            {
                callToken.ThrowIfCancellationRequested();
                yield return frame;
            }
        }
        finally
        {
            cancellation.TrySetResult(RpcCancellationOutcome.RequestNotSent);
        }
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

    // Owns the worker and response stream together. Stopping the caller forwards cancellation,
    // while worker resources stay alive until both the handler and its callbacks have finished.
    sealed class WorkerInvocation : IRpcResponseWriter, IDisposable
    {
        readonly Lock _gate = new();
        readonly Channel<RpcResponseFrame> _frames = Channel.CreateUnbounded<RpcResponseFrame>();
        readonly CancellationTokenSource _workerCancellation = new();
        readonly TaskCompletionSource<RpcCancellationOutcome> _cancellation;
        readonly CancellationTokenRegistration _callerRegistration;
        readonly Task _worker;
        Task _forwarding = Task.CompletedTask;
        ulong _sequence;
        bool _terminal;
        int _disposed;

        public WorkerInvocation(Worker worker, string route, ReadOnlyMemory<byte> body, TimeSpan? timeout,
            long budgetStartedAt, TaskCompletionSource<RpcCancellationOutcome> cancellation, CancellationToken ct)
        {
            _cancellation = cancellation;
            _callerRegistration = ct.Register(Cancel);
            // Independent dispatch also bounds callers of synchronously blocking handlers.
            _worker = Task.Run(() => RunAsync(worker, route, body, timeout, budgetStartedAt, ct), CancellationToken.None);
        }

        public IAsyncEnumerable<RpcResponseFrame> ReadAsync(CancellationToken ct) => _frames.Reader.ReadAllAsync(ct);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            // Registration disposal waits for Cancel to publish its forwarding task.
            _callerRegistration.Dispose();
            Cancel();
            _ = DisposeWorkerAsync();
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> body, bool isFinal, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_terminal)
                    throw new InvalidOperationException("The RPC call has already completed.");
                var sequence = _sequence++;
                if (!isFinal || !body.IsEmpty)
                    _ = _frames.Writer.TryWrite(new RpcResponseFrame(body.ToArray(), sequence));
                if (isFinal)
                    Complete();
            }

            return ValueTask.CompletedTask;
        }

        async Task RunAsync(Worker worker, string route, ReadOnlyMemory<byte> body, TimeSpan? timeout,
            long budgetStartedAt, CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var remaining = timeout is { } duration
                    ? TimeSpan.FromTicks(Math.Max(0, (duration - Stopwatch.GetElapsedTime(budgetStartedAt)).Ticks))
                    : (TimeSpan?)null;
                await worker.Handler(new RpcRequest(route, body, remaining), this, _workerCancellation.Token)
                    .ConfigureAwait(false);
                Complete();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The token can become cancelled before its registered callback runs.
                Cancel();
            }
            catch (Exception exception)
            {
                Complete(exception);
            }
        }

        void Complete(Exception? exception = null)
        {
            lock (_gate)
            {
                if (_terminal)
                    return;
                _terminal = true;
                _cancellation.TrySetResult(RpcCancellationOutcome.NotRequested);
                _ = _frames.Writer.TryComplete(exception);
            }
        }

        void Cancel()
        {
            lock (_gate)
            {
                if (_terminal)
                    return;
                _terminal = true;
                _forwarding = CancelWorkerAsync();
                _cancellation.TrySetResult(RpcCancellationOutcome.Forwarded);
                _ = _frames.Writer.TryComplete(new OperationCanceledException());
            }
        }

        async Task CancelWorkerAsync()
        {
            try
            {
                await _workerCancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (AggregateException)
            {
                // Worker callbacks cannot fault the caller's cancellation path.
            }
        }

        async Task DisposeWorkerAsync()
        {
            await _worker.ConfigureAwait(false);
            await _forwarding.ConfigureAwait(false);
            _workerCancellation.Dispose();
        }
    }
}
