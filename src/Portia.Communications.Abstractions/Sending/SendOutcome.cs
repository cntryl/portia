namespace Cntryl.Portia.Communications;

/// <summary>What is known about a provider submission.</summary>
public enum SendStatus
{
    /// <summary>The provider accepted the message for sending. This is not evidence of delivery.</summary>
    Accepted = 1,

    /// <summary>The provider definitively refused the message; it was not sent.</summary>
    Rejected = 2,

    /// <summary>
    ///     The request may have reached the provider, but its result is uncertain. Do not send again:
    ///     the provider write is not idempotent. A later delivery event, or an explicit business
    ///     decision, settles it.
    /// </summary>
    Unknown = 3,
}

/// <summary>A provider-neutral reason a submission was definitively refused.</summary>
public enum SendRejectionReason
{
    /// <summary>The provider refused the message for a reason with no more specific code.</summary>
    Other = 1,

    /// <summary>The recipient address or number is invalid or cannot receive this channel.</summary>
    InvalidRecipient = 2,

    /// <summary>The recipient opted out of messages from this sender.</summary>
    OptedOut = 3,

    /// <summary>The recipient is on a provider suppression list.</summary>
    Suppressed = 4,

    /// <summary>The provider refused the message content.</summary>
    ContentRejected = 5,

    /// <summary>The sender is not verified, authorized, or currently allowed to send.</summary>
    SenderNotAuthorized = 6,
}

/// <summary>The outcome of one provider submission. Acceptance is never reported as delivery.</summary>
public sealed record SendOutcome
{
    SendOutcome(SendStatus status, SendRejectionReason? rejectionReason, string? providerReference)
    {
        Status = status;
        RejectionReason = rejectionReason;
        ProviderReference = providerReference;
    }

    /// <summary>The submission's result is uncertain; do not send again.</summary>
    public static SendOutcome Unknown { get; } = new(SendStatus.Unknown, null, null);

    /// <summary>Gets what is known about the submission.</summary>
    public SendStatus Status { get; }

    /// <summary>Gets the reason for a <see cref="SendStatus.Rejected" /> outcome; otherwise <see langword="null" />.</summary>
    public SendRejectionReason? RejectionReason { get; }

    /// <summary>Gets the provider's opaque message reference for an accepted submission, when it returned one.</summary>
    public string? ProviderReference { get; }

    /// <summary>Creates an acceptance.</summary>
    /// <param name="providerReference">The provider's opaque message reference, if any.</param>
    /// <returns>The accepted outcome.</returns>
    public static SendOutcome Accepted(string? providerReference) =>
        new(SendStatus.Accepted, null, providerReference);

    /// <summary>Creates a definitive rejection.</summary>
    /// <param name="reason">The provider-neutral reason.</param>
    /// <returns>The rejected outcome.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The reason is undefined.</exception>
    public static SendOutcome Rejected(SendRejectionReason reason) => Enum.IsDefined(reason)
        ? new SendOutcome(SendStatus.Rejected, reason, null)
        : throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown rejection reason.");
}

/// <summary>
///     The provider throttled the submission and did not take the message. Send it again later, at the
///     caller's pace; the provider integration never retries on its own.
/// </summary>
public sealed class SendThrottledException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SendThrottledException() : base("The provider throttled the send; retry it later.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public SendThrottledException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public SendThrottledException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Creates the exception with the provider's suggested delay.</summary>
    /// <param name="retryAfter">The delay the provider suggested, if any.</param>
    public SendThrottledException(TimeSpan? retryAfter) : this() => RetryAfter = retryAfter;

    /// <summary>Gets the delay the provider suggested before retrying, if it gave one.</summary>
    public TimeSpan? RetryAfter { get; }
}
