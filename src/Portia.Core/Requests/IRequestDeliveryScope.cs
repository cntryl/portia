namespace Cntryl.Portia;

/// <summary>Owns the application dependencies used for one notification delivery.</summary>
public interface IRequestDeliveryScope : IAsyncDisposable
{
    /// <summary>Gets the request bus for this delivery.</summary>
    IRequestBus Bus { get; }

    /// <summary>Gets the actor validator for this delivery.</summary>
    IRequestActorValidator ActorValidator { get; }

    /// <summary>Gets the application time provider, when configured.</summary>
    TimeProvider? TimeProvider { get; }
}
