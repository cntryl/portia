namespace Cntryl.Portia.Consumer;

[RequestRoute("consumer", "modules", "one", "get")]
public sealed record FeatureOneRequest(int Value) : IRequest<int>, ICallable, IQueuable;

[RequestRoute("consumer", "modules", "two", "get")]
public sealed record FeatureTwoRequest(int Value) : IRequest<int>, ICallable, IQueuable;
