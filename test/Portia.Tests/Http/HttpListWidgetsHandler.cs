using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

sealed class HttpListWidgetsHandler : IStreamRequestHandler<HttpListWidgets, string>
{
    public async IAsyncEnumerable<string> HandleAsync(
        IRequestContext<HttpListWidgets> context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return "a";
        await Task.Yield();
        yield return "b";
    }
}
