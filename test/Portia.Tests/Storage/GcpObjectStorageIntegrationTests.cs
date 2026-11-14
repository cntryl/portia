using Cntryl.Portia.Storage.Gcp;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Storage.v1;
using Google.Cloud.Storage.V1;

namespace Cntryl.Portia.Tests.Storage;

/// <summary>Checks the public storage flow through the real Google client and Sqrzl's GCS HTTP surface.</summary>
[Trait("Category", "StorageIntegration")]
public sealed class GcpObjectStorageIntegrationTests
{
    const string AccessId = "portia-test";
    const string Secret = "portia-local-secret";

    /// <summary>Runs the reusable storage contract checks against the GCS adapter.</summary>
    [Fact]
    public async Task ShouldPassSharedConformanceGivenSqrzlGcsServer()
    {
        // Arrange
        var endpoint = Environment.GetEnvironmentVariable("PORTIA_GCS_TEST_ENDPOINT")
            ?? Environment.GetEnvironmentVariable("PORTIA_S3_TEST_ENDPOINT")
            ?? throw new InvalidOperationException("Set PORTIA_S3_TEST_ENDPOINT after starting 'docker compose up -d sqrzl'.");
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var firstBucket = $"portia-gcs-a-{suffix}";
        var secondBucket = $"portia-gcs-b-{suffix}";
        var firstTenant = new TenantId("conformance-a");
        var secondTenant = new TenantId("conformance-b");
        var locations = new Dictionary<string, ObjectStorageLocation>(StringComparer.Ordinal)
        {
            [firstTenant.Value] = new(firstBucket, "projects/p/locations/l/keyRings/r/cryptoKeys/conformance-a"),
            [secondTenant.Value] = new(secondBucket, "projects/p/locations/l/keyRings/r/cryptoKeys/conformance-b")
        };
        var uri = new Uri(endpoint);
        // Sqrzl does not decode the Google client's default gzip-encoded JSON request bodies.
        using var client = new StorageClientImpl(new StorageService(new BaseClientService.Initializer
        {
            ApplicationName = "portia-tests",
            BaseUri = new Uri(uri, "/storage/v1/").ToString(),
            GZipEnabled = false,
            HttpClientInitializer = GoogleCredential.FromAccessToken(Secret)
        }));
        var signer = UrlSigner.FromBlobSigner(UrlSigner.HmacBlobSigner.Create(AccessId, Secret));
        // Sqrzl does not model bucket default encryption or report an object's Cloud KMS key.
        var storage = new GcpObjectStorage(client, signer, new LocationResolver(locations),
            new EphemeralDataProtectionProvider().CreateProtector("Cntryl.Portia.Storage.Gcp.UploadSession.v1"))
        {
            VerifyEncryptionEvidence = false
        };
        using var transfer = new HttpClient();

        // Act
        await client.CreateBucketAsync("portia-test", firstBucket);
        await client.CreateBucketAsync("portia-test", secondBucket);

        // Assert
        try
        {
            await ObjectStorageConformance.VerifyAsync(storage, firstTenant, secondTenant,
                async (part, bytes, ct) =>
                {
                    // Sqrzl serves the GCS XML API for requests whose Host is storage.googleapis.com, which is also
                    // the host that was signed. Send the signed path and query to the local endpoint.
                    using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(uri, part.UploadUri.PathAndQuery))
                    {
                        Content = new ByteArrayContent(bytes.ToArray())
                    };
                    request.Headers.Host = part.UploadUri.Host;
                    using var response = await transfer.SendAsync(request, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new InvalidDataException($"{(int)response.StatusCode} from {part.UploadUri}: {await response.Content.ReadAsStringAsync(ct)}");
                    }

                    var generation = response.Headers.TryGetValues("x-goog-generation", out var values)
                        ? values.Single()
                        : throw new InvalidDataException("The signed part upload returned no x-goog-generation.");
                    return new ObjectPartReceipt(part.PartNumber, generation);
                });
        }
        finally
        {
            await client.DeleteBucketAsync(firstBucket);
            await client.DeleteBucketAsync(secondBucket);
        }
    }

    sealed class LocationResolver(Dictionary<string, ObjectStorageLocation> locations) : IObjectStorageTenantResolver
    {
        public string GetObjectKey(TenantId tenantId, ObjectDigest expected) =>
            $"content/sha256/{expected.Sha256[..2]}/{expected.Sha256}";

        public string GetUploadKey(TenantId tenantId, Uuid uploadId) => $"staging/{uploadId:N}";

        public ValueTask<ObjectStorageLocation> ResolveAsync(TenantId tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(locations[tenantId.Value]);
    }
}
