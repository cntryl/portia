namespace Cntryl.Portia.Consumer;

[RequestRoute("consumer", "business", "*", "deposit")]
public sealed record DepositAccount(Uuid Id, int Amount) : IRequest, ICallable, IQueuable;
