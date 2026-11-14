namespace Cntryl.Portia.Storage.Gcp;

/// <summary>Configures the Google Cloud Storage object storage provider.</summary>
public sealed class GcpObjectStorageBuilder
{
    internal StorageClient? Client { get; private set; }

    internal UrlSigner? Signer { get; private set; }

    /// <summary>Sets the Cloud Storage client used by the adapter.</summary>
    public GcpObjectStorageBuilder UseClient(StorageClient client)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        return this;
    }

    /// <summary>
    /// Sets the V4 URL signer for part and download links. When omitted, the adapter derives the signer from the
    /// client's credential, which requires a service-account key or a credential that can call IAM <c>signBlob</c>.
    /// </summary>
    public GcpObjectStorageBuilder UseSigner(UrlSigner signer)
    {
        Signer = signer ?? throw new ArgumentNullException(nameof(signer));
        return this;
    }
}
