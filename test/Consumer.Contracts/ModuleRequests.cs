namespace Cntryl.Portia.Consumer;

[RequestRoute("consumer", "modules", "one", "get")]
[Discriminator("consumer.modules.feature-one")]
public sealed record FeatureOneRequest(int Value) : IRequest<int>, ICallable, IQueuable;

[RequestRoute("consumer", "modules", "two", "get")]
[Discriminator("consumer.modules.feature-two")]
public sealed record FeatureTwoRequest(int Value) : IRequest<int>, ICallable, IQueuable;
