namespace Cntryl.Portia;

/// <summary>Orders independent authorization policies without arbitrary numeric priorities.</summary>
public enum AuthorizationStage
{
    /// <summary>Checks properties of the authenticated principal.</summary>
    Principal = 100,

    /// <summary>
    ///     Checks the permission a request declares with <see cref="RequiresPermissionAttribute" />. Portia
    ///     runs that declared check first in this stage; authorizers registered here follow it.
    /// </summary>
    Permission = 150,

    /// <summary>Checks access to the request's target resource.</summary>
    ResourceAccess = 200,

    /// <summary>Checks elevated or recently confirmed authentication requirements.</summary>
    StepUp = 300
}
