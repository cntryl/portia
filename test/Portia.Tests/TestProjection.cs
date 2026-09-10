namespace Cntryl.Portia;

sealed class TestProjection
{
    public int Value { get; set; }

    public int HandlerCount { get; set; }

    public int LoadCount { get; set; }

    public async ValueTask LoadAsync(CancellationToken ct)
    {
        LoadCount++;
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
    }
}
