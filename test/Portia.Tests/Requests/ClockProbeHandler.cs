namespace Cntryl.Portia;

sealed class ClockProbeHandler : IRequestHandler<ClockProbe>
{
    public DateTimeOffset? StartedAt { get; set; }

    public Uuid CorrelationId { get; private set; }

    public ValueTask<Result> HandleAsync(IRequestContext<ClockProbe> context, CancellationToken ct)
    {
        StartedAt = context.StartedAt;
        CorrelationId = context.CorrelationId;
        return ValueTask.FromResult(Result.Success);
    }
}
