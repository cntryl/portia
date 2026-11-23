using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

/// <summary>An application contract whose tenant always arrives in an HTTP header.</summary>
interface IHttpTenantScoped
{
    TenantId Tenant { get; }
}

/// <summary>An application contract whose member cannot bind from text.</summary>
interface IHttpLinesScoped
{
    List<HttpOrderLine> Lines { get; }
}

[RequestRoute("*", "http-binding-tests", "tenant-scoped", "get")]
[Discriminator("test.http.tenant-scoped.get")]
sealed record HttpTenantScopedGet(TenantId Tenant, string Name = "none")
    : IRequest<string>, ICallable, IHttpTenantScoped;

[RequestRoute("*", "http-binding-tests", "tenant-scoped", "update")]
[Discriminator("test.http.tenant-scoped.update")]
sealed record HttpTenantScopedUpdate(TenantId Tenant, string Name, bool Active = false)
    : IRequest<string>, ICallable, IHttpTenantScoped;

[RequestRoute("*", "http-binding-tests", "tenant-scoped", "computed")]
[Discriminator("test.http.tenant-scoped.computed")]
sealed record HttpTenantScopedComputed(string Name = "none") : IRequest<string>, ICallable, IHttpTenantScoped
{
    public TenantId Tenant => new("computed");
}

[RequestRoute("*", "http-binding-tests", "tenant-scoped", "list")]
sealed record HttpTenantScopedList(TenantId Tenant) : IStreamRequest<string>, ICallable, IHttpTenantScoped;

[RequestRoute("*", "http-binding-tests", "tenant-scoped", "run")]
[Discriminator("test.http.tenant-scoped.run")]
sealed record HttpTenantScopedCommand(TenantId Tenant, string Name)
    : IRequest, ICallable, IQueuable, IHttpTenantScoped;

sealed class HttpTenantScopedGetHandler : IRequestHandler<HttpTenantScopedGet, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<HttpTenantScopedGet> context, CancellationToken ct) =>
        ValueTask.FromResult<Result<string>>($"{context.Request.Tenant}:{context.Request.Name}");
}

sealed class HttpTenantScopedUpdateHandler : IRequestHandler<HttpTenantScopedUpdate, string>
{
    public ValueTask<Result<string>>
        HandleAsync(IRequestContext<HttpTenantScopedUpdate> context, CancellationToken ct) =>
        ValueTask.FromResult<Result<string>>(
            $"{context.Request.Tenant}:{context.Request.Name}:{context.Request.Active}");
}

sealed class HttpTenantScopedComputedHandler : IRequestHandler<HttpTenantScopedComputed, string>
{
    public ValueTask<Result<string>> HandleAsync(
        IRequestContext<HttpTenantScopedComputed> context,
        CancellationToken ct
    ) =>
        ValueTask.FromResult<Result<string>>($"{context.Request.Tenant}:{context.Request.Name}");
}

sealed class HttpTenantScopedListHandler : IStreamRequestHandler<HttpTenantScopedList, string>
{
    public async IAsyncEnumerable<string> HandleAsync(
        IRequestContext<HttpTenantScopedList> context,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        yield return context.Request.Tenant.ToString();
        await Task.Yield();
    }
}

sealed class HttpTenantScopedCommandHandler : IRequestHandler<HttpTenantScopedCommand>
{
    public ValueTask<Result> HandleAsync(IRequestContext<HttpTenantScopedCommand> context, CancellationToken ct) =>
        ValueTask.FromResult(Result.Success);
}
