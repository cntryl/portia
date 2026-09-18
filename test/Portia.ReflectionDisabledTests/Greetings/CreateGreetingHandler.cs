namespace Cntryl.Portia.ReflectionDisabled;

public sealed class CreateGreetingHandler : IRequestHandler<CreateGreeting, string>
{
    public ValueTask<Result<string>> HandleAsync(IRequestContext<CreateGreeting> context, CancellationToken ct)
        => ValueTask.FromResult(string.IsNullOrWhiteSpace(context.Request.Name)
            ? Result<string>.Failure(new RequestError(RequestErrorKind.Validation, "A name is required."))
            : Result<string>.Success($"Hello, {context.Request.Name}!"));
}
