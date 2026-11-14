namespace Cntryl.Portia.Communications.Twilio;

/// <summary>
///     Submits SMS through Twilio Programmable Messaging. Each call makes exactly one Twilio request.
/// </summary>
public sealed class TwilioSmsSender : ISmsSender
{
    readonly HttpClient _client;
    readonly ITwilioTenantResolver _tenantResolver;
    readonly string? _statusCallbackAddress;

    /// <summary>Creates the sender.</summary>
    /// <param name="client">
    ///     The HTTP client. It must not retry requests. Its base address, when set, replaces
    ///     <c>https://api.twilio.com/</c>.
    /// </param>
    /// <param name="tenantResolver">Resolves each tenant's Twilio configuration.</param>
    /// <param name="statusCallbackAddress">
    ///     The public HTTPS address of the status-callback endpoint, or <see langword="null" /> to request
    ///     no status callbacks.
    /// </param>
    /// <exception cref="ArgumentException">The callback address is not an absolute HTTPS address without a query.</exception>
    public TwilioSmsSender(HttpClient client, ITwilioTenantResolver tenantResolver, Uri? statusCallbackAddress)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _tenantResolver = tenantResolver ?? throw new ArgumentNullException(nameof(tenantResolver));
        _statusCallbackAddress = TwilioStatusCallbacks.Normalize(statusCallbackAddress);
    }

    /// <inheritdoc />
    public async ValueTask<SendOutcome> SendAsync(TenantId tenant, SendId id, SmsMessage message,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        _ = SendId.Require(id, nameof(id));
        var tenantSegment = SendCorrelation.EncodeTenant(tenant);
        var account = await _tenantResolver.ResolveAsync(tenant, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The tenant has no Twilio configuration.");

        var form = new List<KeyValuePair<string, string>>
        {
            new("To", message.To),
            new("Body", message.Body),
            new(account.Sender.StartsWith("MG", StringComparison.Ordinal) ? "MessagingServiceSid" : "From", account.Sender),
        };
        if (_statusCallbackAddress is not null)
            form.Add(new("StatusCallback", TwilioStatusCallbacks.Address(_statusCallbackAddress, tenantSegment, id.Value)));

        // A client base address, such as a local emulator, replaces Twilio's API host.
        var path = $"2010-04-01/Accounts/{account.AccountSid}/Messages.json";
        using var request = new HttpRequestMessage(HttpMethod.Post, _client.BaseAddress is null
            ? new Uri("https://api.twilio.com/" + path)
            : new Uri(path, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{account.ApiKeySid}:{account.ApiKeySecret}")));
        request.Content = new FormUrlEncodedContent(form);

        ct.ThrowIfCancellationRequested();
        HttpStatusCode status;
        TimeSpan? retryAfter;
        JsonDocument? body;
        try
        {
            using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
            status = response.StatusCode;
            retryAfter = response.Headers.RetryAfter?.Delta;
            body = await ReadAsync(response, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (!SendCorrelation.NeverDispatched(exception))
        {
            // A timeout, a broken connection or a cancellation after dispatch: Twilio may have taken the message.
            return SendOutcome.Unknown;
        }

        using (body)
        {
            return Classify(status, retryAfter, body?.RootElement);
        }
    }

    // Twilio's response text can repeat the recipient, so only its status and numeric code are used.
    static SendOutcome Classify(HttpStatusCode status, TimeSpan? retryAfter, JsonElement? body)
    {
        if ((int)status is >= 200 and < 300)
            return SendOutcome.Accepted(body is { } accepted && accepted.TryGetProperty("sid", out var sid)
                && sid.ValueKind == JsonValueKind.String ? sid.GetString() : null);

        var code = body is { } error && error.TryGetProperty("code", out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
        if (status == HttpStatusCode.TooManyRequests || code == 20429)
            throw new SendThrottledException(retryAfter);
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound || code == 20003)
            throw new InvalidOperationException(
                $"The message was not sent: Twilio refused the account or credentials (HTTP {(int)status}, code {code}).");
        if ((int)status is < 400 or >= 500 || status == HttpStatusCode.RequestTimeout)
            return SendOutcome.Unknown;

        return SendOutcome.Rejected(code switch
        {
            21610 => SendRejectionReason.OptedOut,
            21211 or 21614 => SendRejectionReason.InvalidRecipient,
            21408 or 21606 => SendRejectionReason.SenderNotAuthorized,
            21602 or 21617 => SendRejectionReason.ContentRejected,
            _ => SendRejectionReason.Other,
        });
    }

    static async ValueTask<JsonDocument?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var document = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    return document;
                document.Dispose();
                return null;
            }
        }
        catch (JsonException)
        {
            // The status code alone decides the outcome.
            return null;
        }
    }
}
