using System.Reflection;

namespace Cntryl.Portia.Consumer;

static class HttpConsumerScenario
{
    public static Assembly Compile(string declaration, string handle, string mapping, string configuration = "")
        => GeneratorCompilation.Compile($$"""
            using System;
            using System.Globalization;
            using System.Net.Http;
            using System.Text;
            using System.Text.Json;
            using System.Text.Json.Serialization;
            using System.Text.Json.Serialization.Metadata;
            using System.Threading;
            using System.Threading.Tasks;
            using Cntryl.Portia;
            using Cntryl.Portia.Testing;
            using Microsoft.AspNetCore.Builder;
            using Microsoft.AspNetCore.Hosting;
            using Microsoft.AspNetCore.Http;
            using Microsoft.AspNetCore.TestHost;
            using Microsoft.Extensions.DependencyInjection;
            {{declaration}}
            [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
            [JsonSerializable(typeof(Binding))]
            [JsonSerializable(typeof(string))]
            internal sealed class ScenarioJsonContext(JsonSerializerOptions options) : JsonSerializerContext(options)
            {
                protected override JsonSerializerOptions? GeneratedSerializerOptions => null;
                public override JsonTypeInfo? GetTypeInfo(Type type) => new DefaultJsonTypeInfoResolver().GetTypeInfo(type, Options);
            }
            public sealed class Handler : IRequestHandler<Binding, string>
            {
                public ValueTask<Result<string>> HandleAsync(IRequestContext<Binding> context, CancellationToken ct)
                {
                    var request = context.Request;
                    return ValueTask.FromResult(Result<string>.Success({{handle}}));
                }
            }
            public static class HttpScenario
            {
                public static async Task<(int, string)> Run(string path, string? json, bool snake, bool converter)
                {
                    var builder = WebApplication.CreateBuilder();
                    builder.WebHost.UseTestServer();
                    builder.Services.AddPortia().AddRequestHandler<Handler>();
                    builder.Services.AddPortia().ConfigureJson(options =>
                    {
                        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
                        options.PropertyNamingPolicy = snake
                            ? JsonNamingPolicy.SnakeCaseLower
                            : JsonNamingPolicy.CamelCase;
                        if (converter) options.Converters.Add(new HexConverter());
                    });
                    {{configuration}}
                    await using var app = builder.Build();
                    {{mapping}}
                    await app.StartAsync();
                    using var client = app.GetTestClient();
                    using var message = new HttpRequestMessage(json is null ? HttpMethod.Get : HttpMethod.Post, path);
                    if (json is not null) message.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var response = await client.SendAsync(message);
                    return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
                }
            }
            public sealed class HexConverter : JsonConverter<int>
            {
                public override int Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
                    => reader.TokenType == JsonTokenType.String
                        ? int.Parse(reader.GetString()!.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                        : reader.GetInt32();
                public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
            }
            """, new RegistrationCallInterceptorGenerator(), new RequestHttpBindingGenerator());

    public static Task<(int Status, string Body)> RunAsync(Assembly assembly, string path, string? json = null, bool snake = false, bool converter = false)
        => (Task<(int, string)>)assembly.GetType("HttpScenario")!.GetMethod("Run")!.Invoke(null, [path, json, snake, converter])!;
}
