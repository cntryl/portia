namespace Cntryl.Portia;

/// <summary>An RPC invocation at its concrete received route.</summary>
public sealed record RpcInvocation(string Route) : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "rpc";
}
