namespace Cntryl.Portia.Communications.Aws;

/// <summary>Submits email through Amazon SES. Each call makes exactly one SES request.</summary>
/// <remarks>
///     The SES client must be configured with <c>MaxErrorRetry = 0</c>: <c>SendEmail</c> is not
///     idempotent, so an SDK retry after a lost response could send the message twice.
/// </remarks>
public sealed class AwsMailSender : IMailSender
{
    internal const string TenantTag = "portia-tenant";
    internal const string SendTag = "portia-send";
    const int MaximumTagValueLength = 256;

    readonly IAmazonSimpleEmailServiceV2 _client;
    readonly IAwsMailTenantResolver _tenantResolver;

    /// <summary>Creates the sender.</summary>
    /// <param name="client">The SES client, configured with <c>MaxErrorRetry = 0</c>.</param>
    /// <param name="tenantResolver">Resolves each tenant's sender identity.</param>
    /// <exception cref="InvalidOperationException">The client retries failed requests.</exception>
    public AwsMailSender(IAmazonSimpleEmailServiceV2 client, IAwsMailTenantResolver tenantResolver)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _tenantResolver = tenantResolver ?? throw new ArgumentNullException(nameof(tenantResolver));
        if (client.Config.MaxErrorRetry != 0)
            throw new InvalidOperationException(
                "The SES client must be configured with MaxErrorRetry = 0; an SDK retry could send a message twice.");
    }

    /// <inheritdoc />
    public async ValueTask<SendOutcome> SendAsync(TenantId tenant, SendId id, MailMessage message,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        _ = SendId.Require(id, nameof(id));
        var tenantTag = SendCorrelation.EncodeTenant(tenant);
        if (tenantTag.Length > MaximumTagValueLength)
            throw new InvalidOperationException("The tenant ID is too long to correlate SES delivery events.");
        var identity = await _tenantResolver.ResolveAsync(tenant, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(identity?.FromAddress))
            throw new InvalidOperationException("The tenant has no SES from address.");

        var request = new SendEmailRequest
        {
            FromEmailAddress = identity.FromAddress,
            ConfigurationSetName = identity.ConfigurationSetName,
            Destination = new Destination { ToAddresses = [.. message.To] },
            ReplyToAddresses = message.ReplyTo is null ? null : [message.ReplyTo],
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = Utf8(message.Subject),
                    Body = new Body { Text = Utf8(message.Text), Html = message.Html is null ? null : Utf8(message.Html) },
                },
            },
            EmailTags =
            [
                new MessageTag { Name = TenantTag, Value = tenantTag },
                new MessageTag { Name = SendTag, Value = id.Value },
            ],
        };

        ct.ThrowIfCancellationRequested();
        try
        {
            var response = await _client.SendEmailAsync(request, ct).ConfigureAwait(false);
            return SendOutcome.Accepted(response.MessageId);
        }
        catch (Exception exception) when (!SendCorrelation.NeverDispatched(exception))
        {
            // Provider exception text can name recipients, so it is classified here and never rethrown or wrapped.
            return Classify(exception);
        }
    }

    static SendOutcome Classify(Exception exception) => exception switch
    {
        TooManyRequestsException or LimitExceededException => throw new SendThrottledException(),
        AccountSuspendedException or SendingPausedException or MailFromDomainNotVerifiedException =>
            SendOutcome.Rejected(SendRejectionReason.SenderNotAuthorized),
        MessageRejectedException rejected => SendOutcome.Rejected(
            rejected.Message.Contains("not verified", StringComparison.OrdinalIgnoreCase)
                ? SendRejectionReason.SenderNotAuthorized
                : SendRejectionReason.ContentRejected),
        NotFoundException => throw Misconfigured("a configured SES resource, such as the configuration set, was not found"),
        BadRequestException => SendOutcome.Rejected(SendRejectionReason.Other),
        AmazonServiceException { StatusCode: HttpStatusCode.TooManyRequests } => throw new SendThrottledException(),
        AmazonServiceException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } service =>
            throw Misconfigured($"SES refused the credentials or permissions ({service.ErrorCode})"),
        AmazonServiceException service when IsDefinitiveRefusal(service.StatusCode) =>
            SendOutcome.Rejected(SendRejectionReason.Other),
        // A server error, a timeout or a cancellation after dispatch: SES may or may not have taken the message.
        _ => SendOutcome.Unknown,
    };

    static bool IsDefinitiveRefusal(HttpStatusCode status) =>
        (int)status is >= 400 and < 500 && status != HttpStatusCode.RequestTimeout;

    static InvalidOperationException Misconfigured(string reason) =>
        new($"The message was not sent: {reason}.");

    static Content Utf8(string data) => new() { Data = data, Charset = "UTF-8" };
}
