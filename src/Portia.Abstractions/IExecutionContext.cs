using System.Security.Claims;

namespace Cntryl.Portia;

/// <summary>Describes an execution and the request or event responsible for its new effects.</summary>
public interface IExecutionContext
{
    /// <summary>
    ///     Gets the principal executing this operation, as an independent copy. Each read returns a
    ///     fresh one, so no participant in a dispatch can change what another sees: an authorizer
    ///     that adds a claim to what it read here does not alter the principal the handler is given,
    ///     and neither alters the caller's own principal.
    ///     <para>
    ///         That copy is not free — it duplicates every identity and claim — so read it once
    ///         into a local rather than repeatedly, and do not use it as the key to anything: two reads
    ///         are equal in content but are never the same instance.
    ///     </para>
    /// </summary>
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
