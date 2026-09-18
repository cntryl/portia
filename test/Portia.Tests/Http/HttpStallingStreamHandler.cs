using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

/// <summary>Yields one multi-line item and then stalls, standing in for an idle event source.</summary>
sealed class HttpStallingStreamHandler : IStreamRequestHandler<HttpStallingStream, string>
{
    public async IAsyncEnumerable<string> HandleAsync(
        IRequestContext<HttpStallingStream> context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return "first\r\nsecond\rthird\nfourth";
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
    }
}
