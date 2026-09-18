namespace Cntryl.Portia;

[RequestRoute("portia-integration", "rpc", "no-worker-registered", "ping")]
[Discriminator("test.rpc.no-worker-registered")]
sealed record NoWorkerRegisteredPing : IRequest, ICallable;
