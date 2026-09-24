namespace Cntryl.Portia;

/// <summary>
///     Reads a Fitz subscription whose notifications are only wake-ups: each says durable state may
///     have changed, never what changed, so any number of them buffered means the same as one.
/// </summary>
/// <remarks>
///     Fitz buffers each subscription handle up to a bound (256 notifications by default) and ends the
///     handle with <see cref="SubscriptionBackpressureException" /> once its reader falls behind. A
///     reader that stays busy only reads between passes, so every read drains what is already
///     buffered; and since the caller re-reads durable state after any wake-up, an overflow that still
///     happens is replaced by a fresh subscription and reported as one wake-up rather than a failure.
/// </remarks>
/// <typeparam name="T">The notification type, whose content is ignored.</typeparam>
sealed class FitzWakeupSubscription<T> : IAsyncDisposable
{
    readonly string _endedMessage;
    readonly CancellationTokenSource _stop = new();
    readonly Func<CancellationToken, Task<SubscriptionHandle<T>>> _subscribe;
    IAsyncEnumerator<T> _notifications;
    Task<bool>? _pending;
    SubscriptionHandle<T> _subscription;

    FitzWakeupSubscription(Func<CancellationToken, Task<SubscriptionHandle<T>>> subscribe, string endedMessage,
        SubscriptionHandle<T> subscription)
    {
        _subscribe = subscribe;
        _endedMessage = endedMessage;
        _subscription = subscription;
        _notifications = subscription.GetAsyncEnumerator(_stop.Token);
    }

    /// <summary>
    ///     Completes when a wake-up is available or the subscription ends. The move is retained until
    ///     <see cref="DrainAsync" /> consumes it; an enumerator cannot be advanced twice concurrently.
    /// </summary>
    public Task Next => _pending ??= _notifications.MoveNextAsync().AsTask();

    /// <summary>Subscribes, so a caller can establish it before its first read of durable state.</summary>
    public static async Task<FitzWakeupSubscription<T>> SubscribeAsync(
        Func<CancellationToken, Task<SubscriptionHandle<T>>> subscribe, string endedMessage, CancellationToken ct)
    {
        var subscription = await subscribe(ct).ConfigureAwait(false);
        return new FitzWakeupSubscription<T>(subscribe, endedMessage, subscription);
    }

    /// <summary>
    ///     Waits for a wake-up and consumes it with every other one already buffered. The token bounds
    ///     this wait, not the subscription: a timed-out wait leaves the move in flight for the next.
    /// </summary>
    public async ValueTask WaitAsync(CancellationToken ct)
    {
        var next = Next;
        await next.WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (!next.IsCompleted)
            throw new OperationCanceledException(ct);
        await DrainAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Consumes every wake-up already buffered without waiting for another. Call it before
    ///     re-reading durable state, which answers each of them.
    /// </summary>
    public async ValueTask DrainAsync(CancellationToken ct)
    {
        while (Next.IsCompleted)
        {
            var pending = _pending!;
            _pending = null;
            try
            {
                if (!await pending.ConfigureAwait(false))
                    throw new InvalidOperationException(_endedMessage);
            }
            catch (SubscriptionBackpressureException)
            {
                await ResubscribeAsync(ct).ConfigureAwait(false);
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            // The move's cancellation or fault belongs to the caller that waited on it; disposal
            // only has to release the subscription.
            if (_pending is not null)
                await ((Task)_pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await _notifications.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _subscription.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _stop.Dispose();
            }
        }
    }

    // The overflowed handle has already ended, so it is released before its replacement exists.
    // Anything committed in between is covered by the caller's next read, which this wake-up causes.
    async ValueTask ResubscribeAsync(CancellationToken ct)
    {
        await _notifications.DisposeAsync().ConfigureAwait(false);
        await _subscription.DisposeAsync().ConfigureAwait(false);
        _subscription = await _subscribe(ct).ConfigureAwait(false);
        _notifications = _subscription.GetAsyncEnumerator(_stop.Token);
    }
}
