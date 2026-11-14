namespace Cntryl.Portia.Communications;

/// <summary>
///     Submits email to a provider. Each call makes exactly one provider request and never retries.
/// </summary>
/// <remarks>
///     This is a transport, not a record of what was sent. A reactor sending mail should have its
///     aggregate record the send as pending before calling this, record the returned outcome, and never
///     send again once an outcome is <see cref="SendStatus.Unknown" />; delivery events settle it.
///     Recipients and content never appear in logs, exceptions or telemetry.
/// </remarks>
public interface IMailSender
{
    /// <summary>Submits one email.</summary>
    /// <param name="tenant">The tenant the message is sent for; it selects the sender identity.</param>
    /// <param name="id">The application's send identity, attached for delivery-event correlation.</param>
    /// <param name="message">The message.</param>
    /// <param name="ct">A token that can cancel the submission. Cancellation after dispatch reports unknown.</param>
    /// <returns>Accepted, rejected with a reason, or unknown. Acceptance is not delivery.</returns>
    /// <exception cref="SendThrottledException">The provider throttled the send and did not take it.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The sender is misconfigured, so the message certainly did not reach the provider.
    /// </exception>
    ValueTask<SendOutcome> SendAsync(TenantId tenant, SendId id, MailMessage message, CancellationToken ct = default);
}

/// <summary>
///     Submits SMS to a provider. Each call makes exactly one provider request and never retries; see
///     <see cref="IMailSender" /> for how an application tracks outcomes.
/// </summary>
public interface ISmsSender
{
    /// <summary>Submits one SMS.</summary>
    /// <param name="tenant">The tenant the message is sent for; it selects the sender and credentials.</param>
    /// <param name="id">The application's send identity, attached for delivery-event correlation.</param>
    /// <param name="message">The message.</param>
    /// <param name="ct">A token that can cancel the submission. Cancellation after dispatch reports unknown.</param>
    /// <returns>Accepted, rejected with a reason, or unknown. Acceptance is not delivery.</returns>
    /// <exception cref="SendThrottledException">The provider throttled the send and did not take it.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The sender is misconfigured, so the message certainly did not reach the provider.
    /// </exception>
    ValueTask<SendOutcome> SendAsync(TenantId tenant, SendId id, SmsMessage message, CancellationToken ct = default);
}
