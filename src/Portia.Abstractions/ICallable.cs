namespace Cntryl.Portia;

/// <summary>
///     Marks a request that can be sent to a remote handler and awaited (Fitz RPC, or another
///     request/response transport). Combine with <see cref="RequestRouteAttribute" />.
/// </summary>
public interface ICallable;
