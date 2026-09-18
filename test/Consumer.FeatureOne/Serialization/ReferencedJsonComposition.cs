using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Consumer;

/// <summary>Exercises generated JSON-context composition from an independently compiled consumer.</summary>
public static class ReferencedJsonComposition
{
    /// <summary>Adds Portia from this independently compiled assembly.</summary>
    public static PortiaBuilder Add(IServiceCollection services) => services.AddPortia();
}
