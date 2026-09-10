namespace Cntryl.Portia;

[RequestRoute("*", "http-binding-tests", "widgets", "list")]
sealed record HttpListWidgets : IStreamRequest<string>, ICallable;
