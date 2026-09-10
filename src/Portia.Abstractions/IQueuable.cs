namespace Cntryl.Portia;

/// <summary>
///     Marks a request that can be enqueued for later dispatch (a Fitz queue, or another durable
///     queue). Combine with <see cref="RequestRouteAttribute" />.
/// </summary>
public interface IQueuable;
