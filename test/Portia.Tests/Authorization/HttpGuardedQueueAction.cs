namespace Cntryl.Portia;

[RequestRoute("*", "http-binding-tests", "guarded-queue", "run")]
[Discriminator("test.http.guarded-queue.run")]
[RequiresPermission("http:guarded-queue")]
sealed record HttpGuardedQueueAction : IRequest, ICallable, IQueuable;
