namespace Cntryl.Portia;

/// <summary>Identifies where a custom-bound value appears in an HTTP request.</summary>
public enum PortiaHttpParameterLocation
{
    /// <summary>A route parameter.</summary>
    Route,

    /// <summary>A query-string parameter.</summary>
    Query,

    /// <summary>An HTTP header.</summary>
    Header,

    /// <summary>An HTTP cookie.</summary>
    Cookie
}
