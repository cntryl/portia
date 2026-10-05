var observed = 0;
for (var iteration = 0; iteration < 100; iteration++)
{
    using var cancellation = new CancellationTokenSource();
    var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
    var pending = WaitAsync();
    await cancellation.CancelAsync();
    try { await pending; } catch (OperationCanceledException) { }
    async Task WaitAsync()
    {
        using var registration = cancellation.Token.Register(() => Interlocked.Increment(ref observed));
        await completion.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
    }
}
Console.WriteLine($"Observed notifications: {observed}/100");
