namespace Cntryl.Portia;

/// <summary>The closed set of final outcomes for one transport delivery.</summary>
public enum RequestDeliveryOutcome
{
    /// <summary>The delivery completed and any required transport acknowledgement succeeded.</summary>
    Completed,

    /// <summary>The delivery was deliberately returned to its transport for another attempt.</summary>
    Abandoned,

    /// <summary>The delivery was handled by the application's terminal policy and acknowledged.</summary>
    Terminal,

    /// <summary>A one-way delivery could not be processed and cannot be redelivered.</summary>
    Lost,

    /// <summary>The caller requested cancellation before the delivery completed.</summary>
    Canceled,

    /// <summary>An unexpected failure prevented the delivery from reaching another final outcome.</summary>
    Fault
}
