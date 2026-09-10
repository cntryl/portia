namespace Cntryl.Portia;

[RequestRoute("test", "rpc", "value", "get")]
[Discriminator("test.rpc.get-value")]
sealed record RpcGetValue : IRequest<int>, ICallable;
