using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Cntryl.Portia;

/// <summary>Verifies HTTP metadata binds members by their declared identity.</summary>
public sealed class HttpBindingMetadataIdentityTests
{
    /// <summary>Renamed JSON properties retain their identity when constructor order differs.</summary>
    [Fact]
    public void ShouldPreserveConstructorMemberIdentityGivenRenamedJsonProperties()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(ReorderedBinding))
                return;
            foreach (var property in info.Properties)
                property.Name = "wire_" + property.Name;
        });
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = resolver };
        using var body = JsonDocument.Parse("{\"wire_a\":11,\"wire_b\":22}");

        var first = PortiaHttpBinding.ReadBody<ReorderedBinding, int>(body.RootElement, options,
            0, "A", "a", false, false, 0);
        var second = PortiaHttpBinding.ReadBody<ReorderedBinding, int>(body.RootElement, options,
            1, "B", "b", false, false, 0);

        Assert.Equal(11, first);
        Assert.Equal(22, second);
    }

    sealed class ReorderedBinding(int a, int b)
    {
        public int B { get; } = b;
        public int A { get; } = a;
    }
}
