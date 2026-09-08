namespace Cntryl.Portia;

/// <summary>
/// Indicates that a fired schedule still uses Portia's legacy bearer-token envelope and must be
/// canceled and recreated with an explicit system identity.
/// </summary>
/// <param name="route">The route on which the legacy schedule fired.</param>
public sealed class LegacyScheduledRequestException(string route) : InvalidOperationException(
    $"Schedule '{route}' uses Portia's legacy bearer-token envelope. Cancel and recreate all existing schedules with an explicit system identity before upgrading schedule workers.");
