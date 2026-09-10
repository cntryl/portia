namespace Cntryl.Portia;

/// <summary>
///     Creates a new user account. Result-bearing, so it can only ever be reached in-proc or over
///     RPC — <see cref="ICallable" /> is the only transport marker a request with a result can
///     implement.
/// </summary>
/// <param name="Email">The user's email address.</param>
/// <param name="DisplayName">The user's display name.</param>
[RequestRoute("*", "identity", "users", "create")]
[Discriminator("test.identity.create-user")]
public sealed record CreateUser(string Email, string DisplayName) : IRequest<Uuid>, ICallable;
