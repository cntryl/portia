namespace Cntryl.Portia;

/// <summary>
///     A request with a declared result, used to verify request-bus dispatch.
/// </summary>
public sealed record GetValue : IRequest<int>;
