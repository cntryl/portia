namespace Cntryl.Portia;

/// <summary>
///     Marks a request that can be scheduled for future or recurring dispatch (a Fitz schedule
///     entry, or another scheduling technology). Combine with <see cref="RequestRouteAttribute" />.
/// </summary>
public interface ISchedulable;
