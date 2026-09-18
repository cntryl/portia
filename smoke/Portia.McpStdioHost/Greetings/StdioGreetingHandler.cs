namespace Cntryl.Portia;

sealed class StdioGreetingHandler : IRequestHandler<StdioGreeting, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<StdioGreeting> context, CancellationToken ct) =>
        ValueTask.FromResult(Result<string>.Success(
            $"Hello, {context.Request.Name}, from {context.Actor.Identity?.Name}."));
}
