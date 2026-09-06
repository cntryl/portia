using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>Describes an execution and the request or event responsible for its new effects.</summary>
public interface IExecutionContext
{
    /// <summary>Gets a snapshot of the principal executing this operation.</summary>
    ClaimsPrincipal Actor { get; }
    /// <summary>Gets this execution's unique identity.</summary>
    Uuid ExecutionId { get; }
    /// <summary>Gets the identity shared by causally related work.</summary>
    Uuid CorrelationId { get; }
    /// <summary>Gets the current request or triggering event identity, used as the cause of new effects.</summary>
    Uuid CauseId { get; }
    /// <summary>Gets the receiver's UTC execution start time.</summary>
    DateTimeOffset StartedAt { get; }
}

/// <summary>A durable actor identifier, without credentials or the principal's claims.</summary>
/// <param name="Subject">The actor's stable subject identifier.</param>
/// <param name="Issuer">The authority identifying that subject.</param>
public sealed record ActorAttribution(string Subject, string Issuer)
{
    internal static ActorAttribution FromPrincipal(ClaimsPrincipal principal)
    {
        var identity = principal.Identities.FirstOrDefault(value => value.IsAuthenticated);
        if (identity is null)
            return new ActorAttribution("anonymous", "Portia");
        var subject = identity.FindFirst(ClaimTypes.NameIdentifier) ?? identity.FindFirst("sub");
        return subject is null || string.IsNullOrWhiteSpace(subject.Value) || string.IsNullOrWhiteSpace(subject.Issuer)
            ? throw new InvalidOperationException("An authenticated event actor requires a NameIdentifier or sub claim with a stable subject and issuer.")
            : new ActorAttribution(subject.Value, subject.Issuer);
    }
}

sealed record EventAttribution(Uuid CorrelationId, Uuid CausationId, Uuid ExecutionId, ActorAttribution Actor)
{
    public static EventAttribution FromContext(IExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var correlation = context.CorrelationId;
        var cause = context.CauseId;
        var execution = context.ExecutionId;
        return correlation == Uuid.Empty || cause == Uuid.Empty || execution == Uuid.Empty
            ? throw new ArgumentException("Execution, correlation, and cause identities cannot be empty.", nameof(context))
            : new EventAttribution(correlation, cause, execution, ActorAttribution.FromPrincipal(context.Actor));
    }
}

static class PrincipalSnapshot
{
    public static ClaimsPrincipal Copy(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return new ClaimsPrincipal(principal.Identities.Select(identity => new ClaimsIdentity(identity)));
    }
}
