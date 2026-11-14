namespace Cntryl.Portia.Communications.Aws;

/// <summary>
///     Turns SES event-publishing records into <see cref="DeliveryEvent" />s and hands them to the
///     application's <see cref="IDeliveryEventReceiver" />.
/// </summary>
/// <remarks>
///     It accepts the SES event JSON on its own, inside an SNS notification, or as an EventBridge event.
///     It does not authenticate the source: feed it only from a source the host already trusts, such as
///     an SQS queue that only the SES event destination can write to.
/// </remarks>
/// <param name="receiver">The application's delivery-event receiver.</param>
public sealed class AwsMailEventIngester(IDeliveryEventReceiver receiver)
{
    readonly IDeliveryEventReceiver _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));

    /// <summary>Ingests one SES event.</summary>
    /// <param name="json">The event JSON, optionally wrapped in an SNS notification or EventBridge event.</param>
    /// <param name="ct">A token that can cancel handling.</param>
    /// <returns>
    ///     <see langword="true" /> when a delivery event was handed to the receiver; <see langword="false" />
    ///     when the record is not a delivery event for mail sent through <see cref="AwsMailSender" />, so
    ///     it can be discarded.
    /// </returns>
    /// <exception cref="FormatException">The text is not JSON.</exception>
    public async ValueTask<bool> IngestAsync(string json, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Parse(json) is not { } deliveryEvent)
            return false;
        await _receiver.ReceiveAsync(deliveryEvent, ct).ConfigureAwait(false);
        return true;
    }

    static DeliveryEvent? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (Text(root, "Type") is not null)
            {
                // An SNS envelope; only a notification carries an SES event.
                if (Text(root, "Type") != "Notification" || Text(root, "Message") is not { } message)
                    return null;
                using var inner = JsonDocument.Parse(message);
                return Read(inner.RootElement);
            }

            return Read(root.ValueKind == JsonValueKind.Object && root.TryGetProperty("detail", out var detail)
                ? detail
                : root);
        }
        catch (JsonException)
        {
            // The payload can hold recipients, so neither it nor the parser's excerpt is repeated.
            throw new FormatException("The SES event is not valid JSON.");
        }
    }

    static DeliveryEvent? Read(JsonElement sesEvent)
    {
        if (sesEvent.ValueKind != JsonValueKind.Object
            || !sesEvent.TryGetProperty("mail", out var mail) || mail.ValueKind != JsonValueKind.Object
            || !mail.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object
            || !SendCorrelation.TryDecodeTenant(Tag(tags, AwsMailSender.TenantTag), out var tenant)
            || Tag(tags, AwsMailSender.SendTag) is not { } send || !SendId.IsValid(send))
            return null;

        (DeliveryEventKind Kind, string Detail)? mapped = Text(sesEvent, "eventType") switch
        {
            "Send" => (DeliveryEventKind.Sent, "send"),
            "Delivery" => (DeliveryEventKind.Delivered, "delivery"),
            "Bounce" => (DeliveryEventKind.Bounced, "bounce"),
            "Complaint" => (DeliveryEventKind.Complained, "complaint"),
            "Reject" => (DeliveryEventKind.Rejected, "reject"),
            "DeliveryDelay" => (DeliveryEventKind.Delayed, "deliveryDelay"),
            "Rendering Failure" => (DeliveryEventKind.Failed, "failure"),
            _ => null,
        };
        if (mapped is not { } known)
            return null;

        var occurredAt = sesEvent.TryGetProperty(known.Detail, out var detail) && Timestamp(detail) is { } specific
            ? specific
            : Timestamp(mail) ?? DateTimeOffset.UtcNow;
        return new DeliveryEvent(tenant, CommunicationChannel.Mail, new SendId(send), known.Kind, occurredAt);
    }

    static string? Tag(JsonElement tags, string name) =>
        tags.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
        && values.GetArrayLength() == 1 && values[0].ValueKind == JsonValueKind.String
            ? values[0].GetString()
            : null;

    static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    static DateTimeOffset? Timestamp(JsonElement element) =>
        Text(element, "timestamp") is { } text && DateTimeOffset.TryParse(text,
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal,
            out var value)
            ? value
            : null;
}
