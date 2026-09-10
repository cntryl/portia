namespace Cntryl.Portia;

/// <summary>Logical request identity propagated independently of credentials and execution attempts.</summary>
/// <param name="RequestId">The logical request identity.</param>
/// <param name="CorrelationId">The related workflow identity.</param>
/// <param name="CausationId">The request or event that directly caused this request.</param>
public sealed record RequestMetadata(Uuid RequestId, Uuid CorrelationId, Uuid? CausationId = null)
{
    /// <summary>Creates a new independent logical request.</summary>
    /// <returns>Metadata whose request and correlation identities are the same new value.</returns>
    public static RequestMetadata Create()
    {
        var id = Uuid.CreateVersion4();
        return new RequestMetadata(id, id);
    }

    /// <summary>Creates a new child request with explicit causal inheritance.</summary>
    /// <param name="parent">The execution this request is caused by.</param>
    /// <returns>
    ///     Metadata with a new request identity, the parent's correlation, and the parent's cause
    ///     as its causation.
    /// </returns>
    public static RequestMetadata FromParent(IExecutionContext parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        var metadata = new RequestMetadata(Uuid.CreateVersion4(), parent.CorrelationId, parent.CauseId);
        metadata.Validate();
        return metadata;
    }

    /// <summary>Rejects empty identities in propagated metadata.</summary>
    /// <exception cref="ArgumentException">Any carried identity is <see cref="Uuid.Empty" />.</exception>
    public void Validate()
    {
        if (RequestId == Uuid.Empty || CorrelationId == Uuid.Empty || CausationId == Uuid.Empty)
        {
            throw new ArgumentException("Request, correlation, and supplied causation identities cannot be empty.");
        }
    }
}
