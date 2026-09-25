namespace Cntryl.Portia.Storage.Aws;

/// <summary>Registers the AWS S3 object storage provider.</summary>
public static class AwsObjectStorageServiceCollectionExtensions
{
    /// <summary>Registers S3 object storage using a configured S3 client and tenant resolver.</summary>
    public static IServiceCollection AddAwsObjectStorage(
        this IServiceCollection services,
        Action<AwsObjectStorageBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new AwsObjectStorageBuilder();
        configure(builder);
        var client = builder.Client ?? throw new InvalidOperationException("An IAmazonS3 client must be configured.");
        services.AddSingleton<IObjectStorage>(provider => new AwsObjectStorage(
            client,
            provider.GetRequiredService<IObjectStorageTenantResolver>(),
            provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Cntryl.Portia.Storage.Aws.UploadSession.v1")));
        return services;
    }
}
