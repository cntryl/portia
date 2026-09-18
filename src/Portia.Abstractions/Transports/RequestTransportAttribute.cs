namespace Cntryl.Portia;

/// <summary>Declares the stable transport ID represented by a request marker interface.</summary>
[AttributeUsage(AttributeTargets.Interface)]
public sealed class RequestTransportAttribute(string id) : Attribute
{
    /// <summary>Gets the stable transport ID.</summary>
    public RequestTransportId Id { get; } = new(id);
}
