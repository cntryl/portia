namespace Cntryl.Portia.Storage;

/// <summary>Registration methods for object storage providers.</summary>
public static class ObjectStorageServiceCollectionExtensions
{
    /// <summary>Registers an object storage implementation.</summary>
    public static IServiceCollection AddObjectStorage<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStorage>(this IServiceCollection services)
        where TStorage : class, IObjectStorage
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IObjectStorage, TStorage>();
        return services;
    }
}
