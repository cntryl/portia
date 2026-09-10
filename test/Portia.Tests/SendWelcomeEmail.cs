namespace Cntryl.Portia;

/// <summary>
///     Sends a welcome email to a newly created user. No result, so it can be reached in-proc, over
///     RPC, over a queue, over live notification, or via schedule.
/// </summary>
/// <param name="UserId">The identity of the user to welcome.</param>
[RequestRoute("*", "identity", "users", "welcome")]
[Discriminator("test.identity.send-welcome")]
public sealed record SendWelcomeEmail(Uuid UserId) : IRequest, ICallable, IQueuable, INotifiable, ISchedulable;
