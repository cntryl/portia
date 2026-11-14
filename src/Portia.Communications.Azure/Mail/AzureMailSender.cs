namespace Cntryl.Portia.Communications.Azure;

/// <summary>
///     Submits email through Azure Communication Services. Each call makes exactly one send request and
///     does not wait for the service to finish processing it.
/// </summary>
/// <remarks>
///     The client must be created with <c>Retry.MaxRetries = 0</c>, as <c>AddAzureMail</c> does: the send
///     is not documented as idempotent, so an SDK retry after a lost response could send the message twice.
///     The service reports delivery through Event Grid by its own message ID only, so this provider does
///     not produce <see cref="DeliveryEvent" />s; <see cref="SendOutcome.ProviderReference" /> is that ID.
/// </remarks>
public sealed class AzureMailSender : IMailSender
{
    readonly EmailClient _client;
    readonly IAzureMailTenantResolver _tenantResolver;

    /// <summary>Creates the sender.</summary>
    /// <param name="client">The email client, created with <c>Retry.MaxRetries = 0</c>.</param>
    /// <param name="tenantResolver">Resolves each tenant's sender identity.</param>
    public AzureMailSender(EmailClient client, IAzureMailTenantResolver tenantResolver)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _tenantResolver = tenantResolver ?? throw new ArgumentNullException(nameof(tenantResolver));
    }

    /// <inheritdoc />
    public async ValueTask<SendOutcome> SendAsync(TenantId tenant, SendId id, MailMessage message,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        _ = SendId.Require(id, nameof(id));
        if (tenant.Value is null)
            throw new ArgumentException("A tenant ID is required.", nameof(tenant));
        var identity = await _tenantResolver.ResolveAsync(tenant, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(identity?.FromAddress))
            throw new InvalidOperationException("The tenant has no sender address.");

        var request = new EmailMessage(
            identity.FromAddress,
            new EmailRecipients(message.To.Select(static address => new EmailAddress(address))),
            new EmailContent(message.Subject) { PlainText = message.Text, Html = message.Html });
        if (message.ReplyTo is not null)
            request.ReplyTo.Add(new EmailAddress(message.ReplyTo));

        ct.ThrowIfCancellationRequested();
        try
        {
            var operation = await _client.SendAsync(WaitUntil.Started, request, ct).ConfigureAwait(false);
            return SendOutcome.Accepted(operation.Id);
        }
        catch (Exception exception) when (!SendCorrelation.NeverDispatched(exception))
        {
            // Provider exception text can name recipients, so it is classified here and never rethrown or wrapped.
            return Classify(exception);
        }
    }

    static SendOutcome Classify(Exception exception) => exception switch
    {
        RequestFailedException { Status: 429 } throttled => throw new SendThrottledException(RetryAfter(throttled)),
        RequestFailedException { Status: 401 or 403 or 404 } refused => throw new InvalidOperationException(
            $"The message was not sent: Azure Communication Services refused the resource, credentials or sender (HTTP {refused.Status})."),
        RequestFailedException { Status: >= 400 and < 500 and not 408 } => SendOutcome.Rejected(SendRejectionReason.Other),
        // A server error, no response, a timeout or a cancellation after dispatch: the service may have taken the message.
        _ => SendOutcome.Unknown,
    };

    static TimeSpan? RetryAfter(RequestFailedException exception) =>
        exception.GetRawResponse() is { } response && response.Headers.TryGetValue("Retry-After", out var text)
        && int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
            out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
}
