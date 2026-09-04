namespace Cntryl.Portia.Consumer;

[RequestRoute("consumer", "scopes", "delivery", "run")]
public sealed record ScopeRequest(Uuid Id, int Behavior = 0) : IRequest, ICallable, IQueuable;

public sealed record NestedScopeRequest(Uuid Id) : IRequest;
