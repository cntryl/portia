namespace Cntryl.Portia;

[RequiresPermission("guarded:stream")]
sealed record GuardedSequence : IStreamRequest<int>;
