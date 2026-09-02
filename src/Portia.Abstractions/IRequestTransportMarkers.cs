namespace Cntryl.Portia;

/// <summary>
/// Marks a request that can be sent to a remote handler and awaited (Fitz RPC, or another
/// request/response transport). Combine with <see cref="RequestRouteAttribute" />.
/// </summary>
public interface ICallable;

/// <summary>
/// Marks a request that can be enqueued for later dispatch (a Fitz queue, or another durable
/// queue). Combine with <see cref="RequestRouteAttribute" />.
/// </summary>
public interface IQueuable;

/// <summary>
/// Marks a request that can be published over live (ephemeral) fanout (Fitz notice, or another
/// live-delivery transport). Combine with <see cref="RequestRouteAttribute" />.
/// </summary>
public interface INotifiable;

/// <summary>
/// Marks a request that can be scheduled for future or recurring dispatch (a Fitz schedule
/// entry, or another scheduling technology). Combine with <see cref="RequestRouteAttribute" />.
/// </summary>
public interface ISchedulable;
