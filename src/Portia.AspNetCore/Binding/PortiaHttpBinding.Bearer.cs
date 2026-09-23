using Microsoft.AspNetCore.Http;

namespace Cntryl.Portia;

public static partial class PortiaHttpBinding
{
    /// <summary>Reads exactly one nonempty Bearer credential, or null when authorization is absent.</summary>
    /// <param name="context">The current HTTP request.</param>
    /// <returns>The raw credential, or <see langword="null" /> when no Authorization header was sent.</returns>
    /// <exception cref="Microsoft.AspNetCore.Http.BadHttpRequestException">
    ///     The header is present but does not carry exactly one nonempty Bearer credential.
    /// </exception>
    public static string? ReadBearerCredential(HttpContext context)
    {
        var values = context.Request.Headers.Authorization;
        if (values.Count == 0)
        {
            return null;
        }

        if (values.Count != 1)
        {
            throw new BadHttpRequestException("Authorization must contain one Bearer credential.");
        }

        var value = values[0];
        if (value is null || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new BadHttpRequestException("Authorization must contain one Bearer credential.");
        }

        var credential = value.AsSpan(7).Trim();
        return credential.IsEmpty || credential.Contains(' ')
            ? throw new BadHttpRequestException("Authorization must contain one Bearer credential.")
            : credential.ToString();
    }

    /// <summary>Rejects an authenticated identity that cannot be replayed by a durable worker.</summary>
    public static string? ReadPortableBearerCredential(HttpContext context)
    {
        var credential = ReadBearerCredential(context);
        if (credential is null && context.User.Identities.Any(identity => identity.IsAuthenticated))
        {
            throw new BadHttpRequestException(
                "Asynchronous delivery of an authenticated request requires a Bearer credential.");
        }

        return credential;
    }
}
