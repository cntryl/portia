namespace Cntryl.Portia;

/// <summary>Defines how an inbound delivery relates to its propagated producer trace.</summary>
public enum RequestTraceRelationship
{
    /// <summary>The propagated context is the parent of the process activity.</summary>
    Parent,

    /// <summary>The process activity starts a new trace linked to the propagated context.</summary>
    Link
}
