namespace Cntryl.Portia.Communications.Twilio;

/// <summary>
///     A tenant's Twilio account, API key and sender. <see cref="ToString" /> never includes the secrets.
/// </summary>
public sealed class TwilioTenant
{
    /// <summary>Creates the tenant's Twilio configuration.</summary>
    /// <param name="accountSid">The account or subaccount SID that owns the messages.</param>
    /// <param name="apiKeySid">The API key SID used to authenticate sends.</param>
    /// <param name="apiKeySecret">The API key's secret.</param>
    /// <param name="sender">A Messaging Service SID (<c>MG…</c>) or an E.164 from number.</param>
    /// <exception cref="ArgumentException">A value is missing or malformed.</exception>
    public TwilioTenant(string accountSid, string apiKeySid, string apiKeySecret, string sender)
    {
        if (string.IsNullOrEmpty(accountSid) || !accountSid.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("A Twilio account SID must be ASCII letters and digits.", nameof(accountSid));
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKeySid);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKeySecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(sender);
        if (apiKeySid.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException("A Twilio API key SID cannot contain ':'.", nameof(apiKeySid));
        AccountSid = accountSid;
        ApiKeySid = apiKeySid;
        ApiKeySecret = apiKeySecret;
        Sender = sender;
    }

    /// <summary>Gets the account or subaccount SID.</summary>
    public string AccountSid { get; }

    /// <summary>Gets the API key SID.</summary>
    public string ApiKeySid { get; }

    /// <summary>Gets the API key secret.</summary>
    public string ApiKeySecret { get; }

    /// <summary>Gets the Messaging Service SID or from number.</summary>
    public string Sender { get; }

    /// <summary>
    ///     Gets the account's auth token, which Twilio signs status callbacks with. Without it every
    ///     status callback for the tenant is refused.
    /// </summary>
    public string? AuthToken { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"TwilioTenant {{ AccountSid = {AccountSid}, Secrets = [redacted] }}";
}

/// <summary>Resolves a Portia tenant to its Twilio account, credentials and sender.</summary>
public interface ITwilioTenantResolver
{
    /// <summary>Resolves the Twilio configuration for a tenant.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="ct">A token that can cancel resolution.</param>
    /// <returns>The tenant's Twilio configuration.</returns>
    ValueTask<TwilioTenant> ResolveAsync(TenantId tenantId, CancellationToken ct = default);
}
