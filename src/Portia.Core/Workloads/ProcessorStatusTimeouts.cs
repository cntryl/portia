namespace Cntryl.Portia;

static class ProcessorStatusTimeouts
{
    internal static readonly TimeSpan Probe = TimeSpan.FromSeconds(2);

    // Allow the checkpoint, source, journal, and ownership probes to time out before abandoning
    // the owner's partial response. Reserve time for the response itself.
    internal static readonly TimeSpan OwnerResponse = Probe * 4 + TimeSpan.FromSeconds(1);
}
