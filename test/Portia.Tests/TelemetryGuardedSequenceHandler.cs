using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

sealed class TelemetryGuardedSequenceHandler : IStreamRequestHandler<TelemetryGuardedSequence, int>
{
    public async IAsyncEnumerable<int> HandleAsync(
        IRequestContext<TelemetryGuardedSequence> context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return 1;
        await Task.CompletedTask;
    }
}
