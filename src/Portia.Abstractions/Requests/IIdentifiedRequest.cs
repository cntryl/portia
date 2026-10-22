namespace Cntryl.Portia;

/// <summary>A request whose required typed input identifies one logical business operation.</summary>
public interface IIdentifiedRequest : IRequestBase
{
    /// <summary>Gets the stable operation identity; an empty identity is invalid.</summary>
    Uuid RequestId { get; }
}
