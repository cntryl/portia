namespace Cntryl.Portia;

/// <summary>A schedule delivery; the current transport provides no occurrence identity.</summary>
/// <param name="Route">The concrete route the occurrence was delivered on.</param>
public sealed record ScheduleInvocation(string Route) : RequestInvocation
{
    /// <inheritdoc />
    public override string TransportName => "schedule";
}
