namespace Cntryl.Portia.Communications;

/// <summary>The channel an outbound message was sent through.</summary>
public enum CommunicationChannel
{
    /// <summary>Email.</summary>
    Mail = 1,

    /// <summary>SMS text message.</summary>
    Sms = 2,
}

/// <summary>A provider-neutral kind of delivery event reported after a submission.</summary>
public enum DeliveryEventKind
{
    /// <summary>The provider queued or handed the message on toward the recipient.</summary>
    Sent = 1,

    /// <summary>Delivery is delayed or temporarily failing; the provider is still trying.</summary>
    Delayed = 2,

    /// <summary>The recipient's server or carrier accepted the message.</summary>
    Delivered = 3,

    /// <summary>The message permanently bounced.</summary>
    Bounced = 4,

    /// <summary>The recipient marked the message as spam.</summary>
    Complained = 5,

    /// <summary>The provider accepted the submission but then refused to send it.</summary>
    Rejected = 6,

    /// <summary>The carrier could not deliver the message.</summary>
    Undelivered = 7,

    /// <summary>The provider failed to send the message.</summary>
    Failed = 8,
}

/// <summary>
///     A verified provider delivery event, correlated to the application's send identity. It carries no
///     provider payload, recipient or content. Any event proves the provider took the message, so it
///     settles a submission whose outcome was unknown.
/// </summary>
/// <param name="Tenant">The tenant the message was sent for.</param>
/// <param name="Channel">The channel the message was sent through.</param>
/// <param name="Id">The application's send identity.</param>
/// <param name="Kind">What happened.</param>
/// <param name="OccurredAt">When the provider reports it happened, or when it was received.</param>
public sealed record DeliveryEvent(
    TenantId Tenant,
    CommunicationChannel Channel,
    SendId Id,
    DeliveryEventKind Kind,
    DateTimeOffset OccurredAt);

/// <summary>
///     Receives verified delivery events from a provider integration, typically by sending a command to
///     the aggregate that recorded the send. Providers redeliver events, so handling must be idempotent.
/// </summary>
public interface IDeliveryEventReceiver
{
    /// <summary>Receives one delivery event.</summary>
    /// <param name="deliveryEvent">The event.</param>
    /// <param name="ct">A token that can cancel handling.</param>
    /// <returns>
    ///     A task that completes once the event is recorded. A failure is reported to the provider or
    ///     event source so the event is delivered again.
    /// </returns>
    ValueTask ReceiveAsync(DeliveryEvent deliveryEvent, CancellationToken ct = default);
}
