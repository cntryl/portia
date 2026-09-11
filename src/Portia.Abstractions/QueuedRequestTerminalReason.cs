namespace Cntryl.Portia;

/// <summary>Identifies why a queued delivery became terminal.</summary>
public enum QueuedRequestTerminalReason
{
    /// <summary>The configured retry threshold was reached.</summary>
    RetryLimitReached = 0,

    /// <summary>The request handler reported a failure that cannot succeed on redelivery.</summary>
    PermanentFailure,

    /// <summary>The actor carried by the delivery could not be validated.</summary>
    ActorValidationFailure
}
