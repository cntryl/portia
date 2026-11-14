namespace Cntryl.Portia.Storage.Gcp;

/// <summary>Registers the Google Cloud Storage object storage provider.</summary>
public static class GcpObjectStorageServiceCollectionExtensions
{
    /// <summary>Registers Cloud Storage object storage using a configured client and tenant resolver.</summary>
    public static IServiceCollection AddGcpObjectStorage(
        this IServiceCollection services,
        Action<GcpObjectStorageBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new GcpObjectStorageBuilder();
        configure(builder);
        var client = builder.Client ?? throw new InvalidOperationException("A StorageClient must be configured.");
        services.AddSingleton<IObjectStorage>(provider => new GcpObjectStorage(
            client,
            builder.Signer ?? CreateSigner(client),
            provider.GetRequiredService<IObjectStorageTenantResolver>(),
            provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Cntryl.Portia.Storage.Gcp.UploadSession.v2")));
        return services;
    }

    static UrlSigner CreateSigner(StorageClient client)
    {
        try
        {
            return client.CreateUrlSigner();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException(
                "The Cloud Storage client's credential cannot sign URLs. Use a service-account key or a credential " +
                "that can call IAM signBlob, or configure a signer with UseSigner.", exception);
        }
    }
}
