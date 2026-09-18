namespace Cntryl.Portia;

[RequestRoute("*", "http-binding-tests", "guarded", "run")]
[Discriminator("test.http.guarded.run")]
[RequiresPermission("http:guarded")]
sealed record HttpGuardedAction : IRequest, ICallable;
