using System.Buffers.Text;
using System.Net.Http;
using System.Text;

namespace Cntryl.Portia.Communications;

/// <summary>Shared provider plumbing: carrying a tenant through a provider and classifying transport failures.</summary>
static class SendCorrelation
{
    /// <summary>Encodes a tenant as text every provider tag and URL path segment can carry.</summary>
    internal static string EncodeTenant(TenantId tenant) => tenant.Value is { } value
        ? Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value))
        : throw new ArgumentException("A tenant ID is required.", nameof(tenant));

    /// <summary>Decodes a tenant written by <see cref="EncodeTenant" />.</summary>
    internal static bool TryDecodeTenant(string? text, out TenantId tenant)
    {
        tenant = default;
        if (string.IsNullOrEmpty(text) || !Base64Url.IsValid(text))
            return false;
        try
        {
            var value = new UTF8Encoding(false, true).GetString(Base64Url.DecodeFromChars(text));
            return TenantId.TryParse(value, null, out tenant) && EncodeTenant(tenant) == text;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Whether a failure proves no request reached the provider: the name did not resolve or no
    ///     connection was established. A general connection error can also mean a reset after sending,
    ///     so it must remain uncertain.
    /// </summary>
    internal static bool NeverDispatched(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException
                {
                    HttpRequestError: HttpRequestError.NameResolutionError
                    or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError
                })
                return true;
        }

        return false;
    }
}
