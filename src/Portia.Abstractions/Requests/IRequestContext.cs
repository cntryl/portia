namespace Cntryl.Portia;

/// <summary>Describes one execution of a logical request.</summary>
public interface IRequestContext : IExecutionContext
{
    /// <summary>Gets the logical request identity, preserved by propagated redeliveries.</summary>
    Uuid RequestId { get; }

    /// <summary>Gets the logical request or event that caused this request.</summary>
    Uuid? CausationId { get; }

    /// <summary>Gets factual information about this execution's immediate ingress.</summary>
    RequestInvocation Invocation { get; }
}

/// <summary>Carries typed business input and its execution context.</summary>
/// <typeparam name="TRequest">The request type.</typeparam>
public interface IRequestContext<out TRequest> : IRequestContext
{
    /// <summary>Gets the business input.</summary>
    TRequest Request { get; }
}
