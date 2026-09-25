namespace Cntryl.Portia.Storage.Aws;

/// <summary>Configures the AWS S3 object storage provider.</summary>
public sealed class AwsObjectStorageBuilder
{
    internal IAmazonS3? Client { get; private set; }

    /// <summary>Sets the AWS S3 client used by the adapter.</summary>
    public AwsObjectStorageBuilder UseClient(IAmazonS3 client)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        return this;
    }
}
