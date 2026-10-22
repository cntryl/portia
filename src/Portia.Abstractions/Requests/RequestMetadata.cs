namespace Cntryl.Portia;

/// <summary>Logical request identity propagated independently of credentials and execution attempts.</summary>
/// <param name="RequestId">The logical request identity.</param>
/// <param name="CorrelationId">The related workflow identity.</param>
/// <param name="CausationId">The request or event that directly caused this request.</param>
public sealed record RequestMetadata(Uuid RequestId, Uuid CorrelationId, Uuid? CausationId = null)
{
    /// <summary>Creates source metadata from a typed operation identity, with optional parent causation.</summary>
    /// <param name="request">The request whose explicit identity is used when it declares one.</param>
    /// <param name="parent">The parent execution, or null for an independent request.</param>
    /// <returns>Logical metadata independent of the receiver's execution attempt.</returns>
    /// <exception cref="ArgumentException">The request declares an empty operation identity.</exception>
    public static RequestMetadata FromRequest(IRequestBase request, IExecutionContext? parent = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (IdentityError(request) is { } error)
            throw new ArgumentException(error.Message, nameof(request));
        var id = request is IIdentifiedRequest identified ? identified.RequestId : Uuid.CreateVersion4();
        var metadata = new RequestMetadata(id, parent?.CorrelationId ?? id, parent?.CauseId);
        metadata.Validate();
        return metadata;
    }

    /// <summary>Validates carried metadata against the request's declared operation identity.</summary>
    /// <param name="request">The request carried with this metadata.</param>
    /// <exception cref="ArgumentException">The typed identity is empty or differs from the carried identity.</exception>
    public void Validate(IRequestBase request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate();
        if (IdentityError(request, this) is { } error)
            throw new ArgumentException(error.Message, nameof(request));
    }

    internal static RequestError? IdentityError(IRequestBase request, RequestMetadata? metadata = null)
    {
        if (request is not IIdentifiedRequest identified)
            return null;
        if (identified.RequestId == Uuid.Empty)
            return new RequestError(RequestErrorKind.Validation, "A typed operation identity is required and cannot be empty.");
        return metadata is not null && metadata.RequestId != identified.RequestId
            ? new RequestError(RequestErrorKind.Validation, "The typed operation identity conflicts with the carried request identity.")
            : null;
    }

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
