namespace Cntryl.Portia;

/// <summary>Indicates that a deserialized request did not declare the transport that delivered it.</summary>
public sealed class InvalidRequestTransportException : InvalidOperationException
{
    /// <summary>Creates an invalid-transport failure.</summary>
    public InvalidRequestTransportException(IRequest request, RequestTransportId transport)
        : base($"Request '{request?.GetType()}' does not declare transport '{transport}'.")
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Transport = transport;
    }

    /// <summary>Gets the deserialized request.</summary>
    public IRequest Request { get; }

    /// <summary>Gets the transport that rejected it.</summary>
    public RequestTransportId Transport { get; }
}
