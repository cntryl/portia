namespace Cntryl.Portia;

[RequestRoute("*", "http-binding-tests", "ping", "ping")]
[Discriminator("test.http.ping")]
sealed record HttpSendPing : IRequest, ICallable, IQueuable;
