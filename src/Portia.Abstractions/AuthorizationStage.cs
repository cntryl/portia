namespace Cntryl.Portia;

/// <summary>Orders independent authorization policies without arbitrary numeric priorities.</summary>
public enum AuthorizationStage
{
    /// <summary>Checks properties of the authenticated principal.</summary>
    Principal = 100,

    /// <summary>Checks access to the request's target resource.</summary>
    ResourceAccess = 200,

    /// <summary>Checks elevated or recently confirmed authentication requirements.</summary>
    StepUp = 300
}
