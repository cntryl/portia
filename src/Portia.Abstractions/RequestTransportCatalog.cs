using System.Collections.Frozen;

namespace Cntryl.Portia;

/// <summary>Provides immutable compile-time request routing and discriminator descriptors.</summary>
public sealed class RequestTransportCatalog
{
    readonly FrozenDictionary<Type, RequestTransportRegistration> _registrations;

    /// <summary>Creates a catalog from generated request descriptors.</summary>
    /// <param name="registrations">One descriptor per request type the application declares.</param>
    public RequestTransportCatalog(IEnumerable<RequestTransportRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        _registrations = registrations.ToFrozenDictionary(registration => registration.RequestType);
    }

    /// <summary>Gets the generated descriptor for a concrete request type.</summary>
    /// <param name="requestType">The concrete request type to look up.</param>
    /// <returns>The descriptor generated for that request type.</returns>
    /// <exception cref="InvalidOperationException">The type is not present in the catalog.</exception>
    public RequestTransportRegistration Get(Type requestType)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        return _registrations.TryGetValue(requestType, out var registration)
            ? registration
            : throw new InvalidOperationException(
                $"Request type '{requestType}' is not present in the generated transport catalog.");
    }
}
