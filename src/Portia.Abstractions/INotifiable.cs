namespace Cntryl.Portia;

/// <summary>
///     Marks a request that can be published over live (ephemeral) fanout (Fitz notice, or another
///     notification transport). Combine with <see cref="RequestRouteAttribute" />.
/// </summary>
public interface INotifiable;
