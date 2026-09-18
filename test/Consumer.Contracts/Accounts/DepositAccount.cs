namespace Cntryl.Portia.Consumer;

[RequestRoute("consumer", "business", "*", "deposit")]
[Discriminator("consumer.business.deposit-account")]
public sealed record DepositAccount(Uuid Id, int Amount) : IRequest, ICallable, IQueuable;
