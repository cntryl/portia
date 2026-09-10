namespace Cntryl.Portia.Consumer;

public sealed class HttpAsyncRouteTests
{
    [Fact]
    public async Task AsyncDispatchUsesEndpointRouteValuesAndCarriedActorToken()
    {
        var assembly = GeneratorCompilation.Compile("""
                                                    using System;
                                                    using System.Net.Http;
                                                    using System.Text;
                                                    using System.Text.Json.Serialization.Metadata;
                                                    using System.Threading;
                                                    using System.Threading.Tasks;
                                                    using Cntryl.Portia;
                                                    using Cntryl.Portia.Testing;
                                                    using Microsoft.AspNetCore.Builder;
                                                    using Microsoft.AspNetCore.Hosting;
                                                    using Microsoft.AspNetCore.TestHost;
                                                    using Microsoft.Extensions.DependencyInjection;
                                                    [RequestRoute("*", "orders", "*", "create")]
                                                    public sealed record Queued(int Amount) : IRequest, ICallable, IQueuable;
                                                    public sealed class Handler : IRequestHandler<Queued>
                                                    {
                                                        public ValueTask<Result> HandleAsync(IRequestContext<Queued> context, CancellationToken ct)
                                                            => throw new InvalidOperationException("HTTP must enqueue when respond-async was requested");
                                                    }
                                                    public sealed class Publisher : IRequestQueuePublisher
                                                    {
                                                        public RequestRouteValues? Values;
                                                        public string? Token;
                                                        public int Amount;
                                                        public ValueTask EnqueueAsync<TRequest>(TRequest request, RequestRouteValues values, string? actorToken, RequestMetadata metadata, CancellationToken ct = default)
                                                            where TRequest : IRequest, IQueuable
                                                        {
                                                            Values = values;
                                                            Token = actorToken;
                                                            Amount = ((Queued)(object)request).Amount;
                                                            return ValueTask.CompletedTask;
                                                        }
                                                    }
                                                    public static class Scenario
                                                    {
                                                        public static async Task<(int, string?, string?, string?, int)> Run()
                                                        {
                                                            var builder = WebApplication.CreateBuilder();
                                                            builder.WebHost.UseTestServer();
                                                            builder.Services.AddPortia()
                                                                .ConfigureJson(options => options.TypeInfoResolver = new DefaultJsonTypeInfoResolver())
                                                                .AddRequestHandler<Handler>();
                                                            builder.Services.AddSingleton<IRequestQueuePublisher, Publisher>();
                                                            await using var app = builder.Build();
                                                            app.MapPortiaPost<Queued>("/tenants/{tenant}/orders/{order}")
                                                                .WithPortiaRouteValues(context => new RequestRouteValues(
                                                                    Realm: (string?)context.Request.RouteValues["tenant"],
                                                                    Resource: (string?)context.Request.RouteValues["order"]));
                                                            await app.StartAsync();
                                                            using var client = app.GetTestClient();
                                                            using var request = new HttpRequestMessage(HttpMethod.Post, "/tenants/acme/orders/order17");
                                                            request.Headers.Add("Prefer", "respond-async");
                                                            request.Headers.Add("Authorization", "Bearer carried-token");
                                                            request.Content = new StringContent("{\"amount\":17}", Encoding.UTF8, "application/json");
                                                            using var response = await client.SendAsync(request);
                                                            var publisher = (Publisher)app.Services.GetRequiredService<IRequestQueuePublisher>();
                                                            return ((int)response.StatusCode, publisher.Values?.Realm, publisher.Values?.Resource, publisher.Token, publisher.Amount);
                                                        }
                                                    }
                                                    """, new RegistrationCallInterceptorGenerator(),
            new RequestHttpBindingGenerator());
        var result =
            await (Task<(int, string?, string?, string?, int)>)assembly.GetType("Scenario")!.GetMethod("Run")!.Invoke(
                null, null)!;
        Assert.Equal((202, "acme", "order17", "carried-token", 17), result);
    }
}
