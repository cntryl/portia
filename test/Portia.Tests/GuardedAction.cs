namespace Cntryl.Portia;

[RequiresPermission("guarded:action")]
sealed record GuardedAction : IRequest;
