namespace Cntryl.Portia;

/// <summary>A live notice delivery at its concrete route.</summary>
public sealed record NoticeInvocation(string Route) : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "notice";
}
