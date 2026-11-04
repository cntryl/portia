using Microsoft.AspNetCore.Http;

namespace Cntryl.Portia;

/// <summary>Verifies same-origin HTTP protection includes the URL scheme.</summary>
public sealed class HttpOriginSchemeTests
{
    /// <summary>Matching host names and default ports do not make different schemes the same origin.</summary>
    /// <param name="scheme">The target request scheme.</param>
    /// <param name="host">The target host and optional port.</param>
    /// <param name="origin">The browser's source origin.</param>
    /// <param name="rejected">Whether the origin must be rejected.</param>
    [Theory]
    [InlineData("https", "example.test", "http://example.test", true)]
    [InlineData("http", "example.test", "https://example.test", true)]
    [InlineData("https", "example.test", "https://example.test", false)]
    [InlineData("http", "example.test", "http://example.test", false)]
    [InlineData("https", "example.test:443", "https://example.test", false)]
    [InlineData("https", "example.test", "https://example.test:443", false)]
    [InlineData("http", "example.test:80", "http://example.test", false)]
    [InlineData("http", "example.test", "http://example.test:80", false)]
    [InlineData("https", "example.test:8443", "https://example.test:8443", false)]
    [InlineData("https", "example.test:8443", "https://example.test", true)]
    [InlineData("https", "example.test", "https://example.test:8443", true)]
    [InlineData("http", "example.test:443", "https://example.test:443", true)]
    public void ShouldCompareOriginSchemeGivenMatchingHostAndDefaultPorts(string scheme, string host, string origin,
        bool rejected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        context.Request.Headers.Origin = origin;

        var result = PortiaHttpBinding.RejectCrossOrigin(context);

        Assert.Equal(rejected, result is not null);
    }
}
