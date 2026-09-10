namespace Cntryl.Portia.Consumer;

[RequestRoute("consumer", "scopes", "delivery", "run")]
[Discriminator("consumer.scopes.scope-request")]
public sealed record ScopeRequest(Uuid Id, int Behavior = 0) : IRequest, ICallable, IQueuable;
