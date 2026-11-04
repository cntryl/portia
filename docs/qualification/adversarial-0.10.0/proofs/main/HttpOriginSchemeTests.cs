using Microsoft.AspNetCore.Http;

namespace Cntryl.Portia;

/// <summary>Verifies same-origin HTTP protection includes the URL scheme.</summary>
public sealed class HttpOriginSchemeTests
{
    /// <summary>Matching host names and default ports do not make different schemes the same origin.</summary>
    /// <param name="scheme">The target request scheme.</param>
    /// <param name="origin">The browser's source origin.</param>
    /// <param name="rejected">Whether the origin must be rejected.</param>
    [Theory]
    [InlineData("https", "http://example.test", true)]
    [InlineData("http", "https://example.test", true)]
    [InlineData("https", "https://example.test", false)]
    [InlineData("http", "http://example.test", false)]
    public void ShouldCompareOriginSchemeGivenMatchingHostAndDefaultPorts(string scheme, string origin, bool rejected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString("example.test");
        context.Request.Headers.Origin = origin;

        var result = PortiaHttpBinding.RejectCrossOrigin(context);

        Assert.Equal(rejected, result is not null);
    }
}
