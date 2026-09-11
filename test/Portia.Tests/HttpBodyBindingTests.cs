using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>
    ///     Content-Length is a claim by the caller, not a measurement, so it must not decide how much
    ///     memory the server reserves. Reserving it lets one small request with a large declared
    ///     length reserve the whole configured maximum, and a few concurrent ones exhaust the host.
    /// </summary>
    [Fact]
    public async Task ShouldNotReserveBufferCapacityForAnUnsentDeclaredContentLength()
    {
        const long declared = 8L * 1024 * 1024;
        await using var provider = new ServiceCollection().AddFrameworkTests()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        // Warm the JSON metadata and document pooling this path touches, so the measurement below
        // reflects the buffer decision rather than first-call setup.
        (await PortiaHttpBinding.ReadJsonBodyAsync(Context(scope, declared), false, CancellationToken.None))
            .Dispose();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        (await PortiaHttpBinding.ReadJsonBodyAsync(Context(scope, declared), false, CancellationToken.None))
            .Dispose();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < declared / 8,
            $"Reading a 2-byte body that declared {declared} bytes allocated {allocated} bytes.");
    }

    static DefaultHttpContext Context(AsyncServiceScope scope, long contentLength)
    {
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.ContentLength = contentLength;
        context.Request.Body = new MemoryStream("{}"u8.ToArray());
        return context;
    }
}
