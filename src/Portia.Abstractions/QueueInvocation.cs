namespace Cntryl.Portia;

/// <summary>A queue delivery with its concrete route and broker-reported attempt.</summary>
/// <param name="Route">The concrete route the request was delivered on.</param>
/// <param name="Attempt">The delivery attempt reported by the broker; Portia does not synthesize it.</param>
public sealed record QueueInvocation(string Route, uint Attempt) : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "queue";
}
