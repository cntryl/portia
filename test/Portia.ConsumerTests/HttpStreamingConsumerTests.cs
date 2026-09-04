using System.Reflection;

namespace Cntryl.Portia.Consumer;

public sealed class HttpStreamingConsumerTests
{
    static readonly Lazy<Assembly> Scenario = new(() => GeneratorCompilation.Compile("""
        using System;
        using System.Collections.Generic;
        using System.Net.Http;
        using System.Security.Claims;
        using System.Runtime.CompilerServices;
        using System.Threading;
        using System.Threading.Tasks;
        using Cntryl.Portia;
        using Microsoft.AspNetCore.Builder;
        using Microsoft.AspNetCore.Hosting;
        using Microsoft.AspNetCore.TestHost;
        using Microsoft.Extensions.DependencyInjection;
        [PortiaModule] public partial class HttpModule { }
        public sealed record StreamRequest(int Mode) : IStreamRequest<string>, ICallable;
        public sealed class Stats
        {
            public int Enumerations, EnumerationDisposals, HandlerDisposals;
            public TaskCompletionSource First = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource ScopeFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public sealed class Authorizer(Stats stats) : IRequestAuthorizer<StreamRequest>, IAsyncDisposable
        {
            public ValueTask<Result> AuthorizeAsync(IRequestContext<StreamRequest> context, ClaimsPrincipal actor, CancellationToken ct = default)
                => ValueTask.FromResult(context.Request.Mode is 401 or 403
                    ? Result.Failure(new RequestError(context.Request.Mode == 401 ? RequestErrorKind.Unauthorized : RequestErrorKind.Forbidden, "Denied"))
                    : Result.Success);
            public ValueTask DisposeAsync() { stats.ScopeFinished.TrySetResult(); return ValueTask.CompletedTask; }
        }
        public sealed class Handler(Stats stats) : IStreamRequestHandler<StreamRequest, string>, IAsyncDisposable
        {
            public async IAsyncEnumerable<string> HandleAsync(IRequestContext<StreamRequest> context, [EnumeratorCancellation] CancellationToken ct)
            {
                stats.Enumerations++;
                try
                {
                    stats.First.TrySetResult();
                    yield return "a";
                    if (context.Request.Mode == 2) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    if (context.Request.Mode == 3) throw new InvalidOperationException("Stream failed after first item");
                    await Task.Yield();
                    yield return "b";
                }
                finally { stats.EnumerationDisposals++; }
            }
            public ValueTask DisposeAsync() { stats.HandlerDisposals++; return ValueTask.CompletedTask; }
        }
        public static class Scenario
        {
            public static async Task<(int, string, int, int, int, bool)> Run(bool sse, int mode)
            {
                var builder = WebApplication.CreateBuilder();
                builder.WebHost.UseTestServer();
                builder.Services.AddSingleton<Stats>();
                builder.Services.AddPortiaModule<HttpModule>();
                await using var app = builder.Build();
                app.MapPortiaGetStream<StreamRequest, string>("/json");
                app.MapPortiaGetSse<StreamRequest, string>("/sse");
                await app.StartAsync();
                using var client = app.GetTestClient();
                using var cancellation = new CancellationTokenSource();
                var stats = app.Services.GetRequiredService<Stats>();
                var responseTask = client.GetAsync((sse ? "/sse" : "/json") + "?mode=" + mode, cancellation.Token);
                if (mode == 2)
                {
                    await stats.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    cancellation.Cancel();
                }
                var status = 0;
                var body = "";
                var failed = false;
                try
                {
                    using var response = await responseTask;
                    status = (int)response.StatusCode;
                    body = await response.Content.ReadAsStringAsync();
                }
                catch (Exception) when (mode is 2 or 3) { failed = true; }
                await stats.ScopeFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return (status, body, stats.Enumerations, stats.EnumerationDisposals, stats.HandlerDisposals, failed);
            }
        }
        """, new RequestBusGenerator(), new PortiaServiceRegistrationGenerator(), new PortiaModuleGenerator(), new RequestHttpBindingGenerator()));

    [Theory]
    [InlineData(false, 401)]
    [InlineData(false, 403)]
    [InlineData(true, 401)]
    [InlineData(true, 403)]
    public async Task AuthorizationFailureReturnsStatusBeforeStreamStarts(bool sse, int mode)
    {
        var (status, _, enumerations, disposals, handlerDisposals, failed) = await RunAsync(sse, mode);
        Assert.Equal(mode, status);
        Assert.False(failed);
        Assert.Equal(0, enumerations);
        Assert.Equal(0, disposals);
        Assert.Equal(0, handlerDisposals);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public async Task StreamEnumeratesOnceAndDisposesAfterCompletionCancellationOrFailure(bool sse, int mode)
    {
        var (status, body, enumerations, disposals, handlerDisposals, failed) = await RunAsync(sse, mode);
        Assert.Equal(1, enumerations);
        Assert.Equal(1, disposals);
        Assert.Equal(1, handlerDisposals);
        Assert.Equal(mode != 0, failed);
        if (mode == 0)
        {
            Assert.Equal(200, status);
            Assert.Contains(sse ? "data: a" : "\"a\"", body, StringComparison.Ordinal);
            Assert.Contains(sse ? "data: b" : "\"b\"", body, StringComparison.Ordinal);
        }
    }

    static Task<(int, string, int, int, int, bool)> RunAsync(bool sse, int mode)
        => (Task<(int, string, int, int, int, bool)>)Scenario.Value.GetType("Scenario")!.GetMethod("Run")!.Invoke(null, [sse, mode])!;
}
