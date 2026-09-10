namespace Cntryl.Portia;

/// <summary>An RPC invocation at its concrete received route.</summary>
/// <param name="Route">The concrete route the call was received on.</param>
public sealed record RpcInvocation(string Route) : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "rpc";
}
