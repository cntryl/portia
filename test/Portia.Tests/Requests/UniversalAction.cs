namespace Cntryl.Portia;

[RequestRoute("test", "shared", "action", "run")]
[Discriminator("test.shared.universal-action")]
sealed record UniversalAction(int Value) : IRequest, ICallable, IQueuable, INotifiable, ISchedulable;
