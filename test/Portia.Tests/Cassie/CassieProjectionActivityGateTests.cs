namespace Cntryl.Portia;

/// <summary>Checks query-connection and projection-batch synchronization.</summary>
public sealed class CassieProjectionActivityGateTests
{
    /// <summary>A batch waits for every earlier read and blocks later reads until it exits.</summary>
    [Fact]
    public async Task ShouldWaitForOpenReadConnectionsBeforeStartingBatch()
    {
        var activity = new CassieProjectionActivityGate();
        var firstRead = activity.EnterRead();
        var secondRead = activity.EnterRead();

        var begin = activity.EnterBatchAsync(default).AsTask();

        Assert.False(begin.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => activity.EnterRead());
        firstRead.Dispose();
        Assert.False(begin.IsCompleted);

        secondRead.Dispose();
        await begin;
        Assert.Throws<InvalidOperationException>(() => activity.EnterRead());

        activity.ExitBatch();
        using var readAfterBatch = activity.EnterRead();
    }
}
