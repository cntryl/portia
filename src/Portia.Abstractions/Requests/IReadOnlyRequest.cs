namespace Cntryl.Portia;

/// <summary>Marks a request whose handling is read-only and safe to invoke with HTTP GET.</summary>
/// <remarks>
///     Implement this marker only when handling the request does not change application state or cause
///     externally visible side effects. Hosts using Portia's <c>RequireSecureHost()</c> profile must
///     use it for requests mapped to GET endpoints.
/// </remarks>
public interface IReadOnlyRequest : IRequestBase;
