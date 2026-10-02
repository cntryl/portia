using System.Runtime.CompilerServices;
using Cntryl.Portia;
using Cntryl.Portia.Testing;
using Microsoft.Extensions.DependencyInjection;

static class RequestScenarioQualification
{
    public static async Task RunAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<RequestRegistry>();
        services.AddScoped<Handler>();
        services.AddSingleton<RequestHandlerRegistration>(new RequestRegistration<Command, Handler>());
        services.AddSingleton<RequestHandlerRegistration>(new RequestRegistration<Query, Handler, int>());
        services.AddSingleton<RequestHandlerRegistration>(new StreamRequestRegistration<Stream, Handler, int>());
        await using var provider = services.BuildServiceProvider();
        var scenario = RequestScenario.For(provider);
        var limited = scenario.When(new Stream(), 1).ExpectSuccess().ExpectItems(1);
        if ((await limited).Count != 1 || (await limited).Count != 1)
            throw new InvalidOperationException("Finite scenario failed.");
        await scenario.When(new Stream(), 10).ExpectItems(1, 2);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ExpectCanceledAsync(() => scenario.When(new Command(), cancellation.Token).AsTask());
        await ExpectCanceledAsync(() => scenario.When(new Query(), cancellation.Token).AsTask());
        await ExpectCanceledAsync(() => scenario.When(new Stream(), cancellation.Token).AsTask());
    }

    static async Task ExpectCanceledAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException("Scenario swallowed caller cancellation.");
    }

    sealed record Command : IRequest;
    sealed record Query : IRequest<int>;
    sealed record Stream : IStreamRequest<int>;
    sealed class Handler : IRequestHandler<Command>, IRequestHandler<Query, int>, IStreamRequestHandler<Stream, int>
    {
        public ValueTask<Result> HandleAsync(IRequestContext<Command> context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Result.Success);
        }
        public ValueTask<Result<int>> HandleAsync(IRequestContext<Query> context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Result<int>.Success(1));
        }
        public async IAsyncEnumerable<int> HandleAsync(IRequestContext<Stream> context, [EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            yield return 1;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return 2;
        }
    }
}
