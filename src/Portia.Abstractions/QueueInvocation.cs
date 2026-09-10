namespace Cntryl.Portia;

/// <summary>A queue delivery with its concrete route and broker-reported attempt.</summary>
public sealed record QueueInvocation(string Route, uint Attempt) : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "queue";
}
