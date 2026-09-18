namespace Cntryl.Portia;

[RequiresPermission("telemetry:guarded")]
sealed record TelemetryGuardedAction : IRequest;
