namespace Cntryl.Portia.Consumer;

public sealed class HttpBindingMetadataIdentityConsumerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShouldPreserveConstructorMemberIdentityGivenRenamedJsonProperties(bool recordStruct)
    {
        var declaration = recordStruct
            ? """
              public readonly record struct Binding(
                  [property: JsonPropertyOrder(2)] int A,
                  [property: JsonPropertyOrder(1)] int B) : IRequest<string>, ICallable;
              """
            : """
              public sealed class Binding(int a, int b) : IRequest<string>, ICallable
              {
                  public int B { get; } = b;
                  public int A { get; } = a;
              }
              """;
        var assembly = HttpConsumerScenario.Compile(
            declaration,
            "request.A.ToString(CultureInfo.InvariantCulture) + \",\" + request.B.ToString(CultureInfo.InvariantCulture)",
            "app.MapPortiaPost<Binding, string>(\"/binding\");",
            """
            builder.Services.AddPortia().ConfigureJson(options => options.TypeInfoResolver =
                new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
                {
                    Modifiers =
                    {
                        info =>
                        {
                            if (info.Type == typeof(Binding))
                                foreach (var property in info.Properties)
                                    property.Name = "wire_" + property.Name;
                        }
                    }
                });
            """);

        var (status, body) = await HttpConsumerScenario.RunAsync(assembly, "/binding", "{\"wire_a\":11,\"wire_b\":22}");

        Assert.Equal(200, status);
        Assert.Equal("\"11,22\"", body);
    }
}
