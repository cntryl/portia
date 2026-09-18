using System.Runtime.CompilerServices;

namespace Cntryl.Portia;

sealed class GuardedSequenceHandler : IStreamRequestHandler<GuardedSequence, int>
{
    public bool WasInvoked { get; private set; }

    public async IAsyncEnumerable<int> HandleAsync(
        IRequestContext<GuardedSequence> context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        WasInvoked = true;
        yield return 1;
        yield return 2;
        yield return 3;
        await Task.CompletedTask;
    }
}
