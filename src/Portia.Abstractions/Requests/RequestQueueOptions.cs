namespace Cntryl.Portia;

/// <summary>Controls one queued request's initial visibility and lifetime at publication.</summary>
/// <param name="Delay">Initial visibility delay in nonnegative whole seconds, or null for immediate visibility.</param>
/// <param name="TtlHours">Positive message lifetime in hours, or null for the provider's default lifetime.</param>
/// <remarks>The lifetime begins at publication and includes the initial delay; redelivery does not restart it.</remarks>
public sealed record RequestQueueOptions(TimeSpan? Delay = null, uint? TtlHours = null)
{
    internal void Validate()
    {
        if (Delay is { } delay && (delay < TimeSpan.Zero || delay.Ticks % TimeSpan.TicksPerSecond != 0))
            throw new ArgumentOutOfRangeException(nameof(Delay), "The queue delay must be nonnegative whole seconds.");
        if (TtlHours == 0)
            throw new ArgumentOutOfRangeException(nameof(TtlHours), "The queue lifetime must be positive whole hours.");
    }
}
