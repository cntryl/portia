namespace Cntryl.Portia;

/// <summary>
///     How a scheduled request is delivered when it fires.
/// </summary>
public enum RequestScheduleDeliveryMode
{
    /// <summary>
    ///     Delivered to exactly one consumer.
    /// </summary>
    One,

    /// <summary>
    ///     Delivered to every live consumer.
    /// </summary>
    Broadcast
}
