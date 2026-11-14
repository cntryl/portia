namespace Cntryl.Portia.Storage.Azure;

/// <summary>Configures the Azure Blob Storage object storage provider.</summary>
public sealed class AzureObjectStorageBuilder
{
    internal BlobServiceClient? Client { get; private set; }

    /// <summary>Sets the blob service client used by the adapter. It must hold a shared-key credential or an Entra ID credential that may request user delegation keys.</summary>
    public AzureObjectStorageBuilder UseClient(BlobServiceClient client)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        return this;
    }
}
