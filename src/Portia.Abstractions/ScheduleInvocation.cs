namespace Cntryl.Portia;

/// <summary>A schedule delivery; the current transport provides no occurrence identity.</summary>
public sealed record ScheduleInvocation(string Route) : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "schedule";
}
