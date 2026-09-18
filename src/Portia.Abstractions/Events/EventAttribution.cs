namespace Cntryl.Portia;

sealed record EventAttribution(Uuid CorrelationId, Uuid CausationId, Uuid ExecutionId, ActorAttribution Actor)
{
    public static EventAttribution FromContext(IExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var correlation = context.CorrelationId;
        var cause = context.CauseId;
        var execution = context.ExecutionId;
        return correlation == Uuid.Empty || cause == Uuid.Empty || execution == Uuid.Empty
            ? throw new ArgumentException("Execution, correlation, and cause identities cannot be empty.",
                nameof(context))
            : new EventAttribution(correlation, cause, execution, ActorAttribution.FromPrincipal(context.Actor));
    }
}
