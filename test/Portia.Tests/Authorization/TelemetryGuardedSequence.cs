namespace Cntryl.Portia;

[RequiresPermission("telemetry:guarded-stream")]
sealed record TelemetryGuardedSequence : IStreamRequest<int>;
