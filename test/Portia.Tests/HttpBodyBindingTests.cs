using System.Text.Json;

namespace Cntryl.Portia;

/// <summary>Covers the body-property lookup generated HTTP bindings call directly.</summary>
public sealed class HttpBodyBindingTests
{
    /// <summary>
    ///     Verifies a case-insensitive property lookup resolves to the first matching property in
    ///     document order, the same rule the exact-match path already follows, rather than whichever
    ///     duplicate happens to appear last.
    /// </summary>
    [Fact]
    public void ShouldBindTheFirstCaseInsensitiveBodyPropertyMatch()
    {
        var options = new JsonSerializerOptions(TestJson.Options()) { PropertyNameCaseInsensitive = true };
        using var body = JsonDocument.Parse("""{"NAME":"first","nAmE":"second"}""");

        var bound = PortiaHttpBinding.ReadBody(body.RootElement, options, "name", false, false, string.Empty);

        Assert.Equal("first", bound);
    }
}
