namespace Cntryl.Portia;

/// <summary>
///     Marks a request that can be enqueued for later dispatch through a queue provider.
///     Combine with <see cref="RequestRouteAttribute" />. Persistence and crash recovery follow the
///     provider's contract.
/// </summary>
[RequestTransport("queue")]
public interface IQueuable;
