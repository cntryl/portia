namespace Cntryl.Portia;

/// <summary>
///     A request with no result, used to verify request-bus dispatch.
/// </summary>
public sealed record ChangeValue(int Value) : IRequest;
