namespace Cntryl.Portia.Storage.Azure;

/// <summary>Registers the Azure Blob Storage object storage provider.</summary>
public static class AzureObjectStorageServiceCollectionExtensions
{
    /// <summary>Registers Azure Blob object storage using a configured blob service client and tenant resolver.</summary>
    public static IServiceCollection AddAzureObjectStorage(
        this IServiceCollection services,
        Action<AzureObjectStorageBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new AzureObjectStorageBuilder();
        configure(builder);
        var client = builder.Client ?? throw new InvalidOperationException("A BlobServiceClient must be configured.");
        services.AddSingleton<IObjectStorage>(provider => new AzureObjectStorage(
            client,
            provider.GetRequiredService<IObjectStorageTenantResolver>(),
            provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Cntryl.Portia.Storage.Azure.UploadSession.v2")));
        return services;
    }
}
