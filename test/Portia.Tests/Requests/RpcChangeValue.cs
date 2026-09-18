namespace Cntryl.Portia;

[RequestRoute("test", "rpc", "value", "change")]
[Discriminator("test.rpc.change-value")]
sealed record RpcChangeValue(int Value) : IRequest, ICallable;
