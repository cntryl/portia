namespace Cntryl.Portia;

[RequiresPermission("guarded:query")]
sealed record GuardedQuery : IRequest<int>;
