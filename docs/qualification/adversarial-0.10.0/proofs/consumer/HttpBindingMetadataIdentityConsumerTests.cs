namespace Cntryl.Portia.Consumer;

public sealed class HttpBindingMetadataIdentityConsumerTests
{
    [Fact]
    public async Task ShouldPreserveConstructorMemberIdentityGivenRenamedJsonProperties()
    {
        var assembly = HttpConsumerScenario.Compile(
            """
            public sealed class Binding(int a, int b) : IRequest<string>, ICallable
            {
                public int B { get; } = b;
                public int A { get; } = a;
            }
            """,
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
