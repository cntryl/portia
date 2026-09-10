namespace Cntryl.Portia;

[RequiresPermission("guarded_and_authorized:action")]
sealed record GuardedAndAuthorizedAction : IRequest;
