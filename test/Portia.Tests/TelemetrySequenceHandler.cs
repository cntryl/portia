using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

sealed class TelemetrySequenceHandler : IStreamRequestHandler<TelemetrySequence, int>
{
    public async IAsyncEnumerable<int> HandleAsync(
        IRequestContext<TelemetrySequence> context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return 1;
        yield return 2;
        yield return 3;
        await Task.CompletedTask;
    }
}
