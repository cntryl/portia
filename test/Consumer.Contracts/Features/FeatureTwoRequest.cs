namespace Cntryl.Portia.Consumer;

[RequestRoute("consumer", "modules", "two", "get")]
[Discriminator("consumer.modules.feature-two")]
public sealed record FeatureTwoRequest(int Value) : IRequest<int>, ICallable, IQueuable;
