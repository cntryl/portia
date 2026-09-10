namespace Cntryl.Portia;

/// <summary>
///     The transports a request declared itself reachable through, by implementing
///     <see cref="ICallable" />, <see cref="IQueuable" />, <see cref="INotifiable" />, and/or
///     <see cref="ISchedulable" />.
/// </summary>
[Flags]
public enum RequestTransports
{
    /// <summary>
    ///     No transport marker.
    /// </summary>
    None = 0,

    /// <summary>
    ///     The request implements <see cref="ICallable" />.
    /// </summary>
    Callable = 1,

    /// <summary>
    ///     The request implements <see cref="IQueuable" />.
    /// </summary>
    Queuable = 2,

    /// <summary>
    ///     The request implements <see cref="INotifiable" />.
    /// </summary>
    Notifiable = 4,

    /// <summary>
    ///     The request implements <see cref="ISchedulable" />.
    /// </summary>
    Schedulable = 8
}
