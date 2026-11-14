namespace Cntryl.Portia.Communications.Twilio;

/// <summary>Maps the endpoint that receives Twilio message status callbacks.</summary>
public static class TwilioStatusCallbackEndpointRouteBuilderExtensions
{
    /// <summary>
    ///     Maps <c>POST {pattern}/{tenant}/{send}</c>. A callback is handed to the application's
    ///     <see cref="IDeliveryEventReceiver" /> only after its <c>X-Twilio-Signature</c> verifies against
    ///     the tenant's auth token and the public address given to <c>UseStatusCallbacks</c>; any other
    ///     callback is refused with 403 and records nothing.
    /// </summary>
    /// <remarks>
    ///     The endpoint allows anonymous requests because the Twilio signature is its authentication.
    ///     <paramref name="pattern" /> must be the path of the public address given to <c>UseStatusCallbacks</c>.
    /// </remarks>
    /// <param name="endpoints">The endpoints.</param>
    /// <param name="pattern">The route prefix, such as <c>/twilio/status</c>.</param>
    /// <returns>A builder for further endpoint conventions.</returns>
    public static IEndpointConventionBuilder MapTwilioStatusCallbacks(this IEndpointRouteBuilder endpoints, string pattern)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        return endpoints.MapPost(pattern.TrimEnd('/') + "/{tenant}/{send}", TwilioStatusCallbacks.HandleAsync)
            .AllowAnonymous();
    }
}

/// <summary>Builds and verifies status-callback addresses.</summary>
static class TwilioStatusCallbacks
{
    internal static string? Normalize(Uri? address)
    {
        if (address is null)
            return null;
        if (!address.IsAbsoluteUri || address.Scheme != Uri.UriSchemeHttps
            || address.Query.Length != 0 || address.Fragment.Length != 0 || address.UserInfo.Length != 0)
            throw new ArgumentException(
                "The status-callback address must be an absolute HTTPS address without a query, fragment or credentials.",
                nameof(address));
        return address.AbsoluteUri.TrimEnd('/');
    }

    internal static string Address(string baseAddress, string tenantSegment, string send) =>
        $"{baseAddress}/{tenantSegment}/{send}";

    internal static async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        var options = context.RequestServices.GetRequiredService<TwilioStatusCallbackOptions>();
        var tenantSegment = request.RouteValues["tenant"] as string;
        var send = request.RouteValues["send"] as string;
        if (options.Address is null || !SendCorrelation.TryDecodeTenant(tenantSegment, out var tenant)
            || !SendId.IsValid(send))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!request.HasFormContentType)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var ct = context.RequestAborted;
        var form = await request.ReadFormAsync(ct).ConfigureAwait(false);
        var account = await context.RequestServices.GetRequiredService<ITwilioTenantResolver>()
            .ResolveAsync(tenant, ct).ConfigureAwait(false);
        var signature = request.Headers["X-Twilio-Signature"];
        if (account?.AuthToken is not { Length: > 0 } token || signature.Count != 1
            || !Verify(Address(options.Address, tenantSegment!, send), form, token, signature[0])
            || form["AccountSid"] != account.AccountSid)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        DeliveryEventKind? kind = form["MessageStatus"].ToString() switch
        {
            "queued" or "sending" or "sent" => DeliveryEventKind.Sent,
            "delivered" => DeliveryEventKind.Delivered,
            "undelivered" => DeliveryEventKind.Undelivered,
            "failed" => DeliveryEventKind.Failed,
            _ => null,
        };
        if (kind is { } known)
        {
            var clock = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
            await context.RequestServices.GetRequiredService<IDeliveryEventReceiver>().ReceiveAsync(
                new DeliveryEvent(tenant, CommunicationChannel.Sms, new SendId(send), known, clock.GetUtcNow()), ct)
                .ConfigureAwait(false);
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    // Twilio signs the callback URL followed by every form field name and value, sorted by name.
    static bool Verify(string url, IFormCollection form, string token, string? signature)
    {
        var text = new StringBuilder(url);
        foreach (var field in form.OrderBy(static field => field.Key, StringComparer.Ordinal))
        {
            foreach (var value in field.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                _ = text.Append(field.Key).Append(value);
        }

        // Twilio defines the callback signature as HMAC-SHA1; no stronger scheme is offered.
#pragma warning disable CA5350
        var expected = HMACSHA1.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(text.ToString()));
#pragma warning restore CA5350
        Span<byte> provided = stackalloc byte[HMACSHA1.HashSizeInBytes];
        return Convert.TryFromBase64String(signature ?? string.Empty, provided, out var written)
            && written == provided.Length
            && CryptographicOperations.FixedTimeEquals(provided, expected);
    }
}

/// <summary>The public status-callback address shared by the sender and the endpoint.</summary>
sealed record TwilioStatusCallbackOptions(string? Address);
