namespace Cntryl.Portia;

[RequestRoute("*", "http-binding-tests", "stalling-stream", "read")]
[Discriminator("test.http.stalling-stream")]
sealed record HttpStallingStream : IStreamRequest<string>, ICallable;
