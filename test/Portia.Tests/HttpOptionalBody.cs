namespace Cntryl.Portia;

[RequestRoute("*", "http-binding-tests", "optional", "post")]
[Discriminator("test.http.optional")]
sealed record HttpOptionalBody(string Value = "fallback") : IRequest<string>, ICallable;
