using System.Web;
using Azure.Storage.Blobs;
using Cntryl.Portia.Storage.Azure;

namespace Cntryl.Portia.Tests.Storage;

/// <summary>Checks the public storage flow through the real Azure SDK and Sqrzl's Azure Blob HTTP surface.</summary>
[Trait("Category", "StorageIntegration")]
public sealed class AzureObjectStorageIntegrationTests
{
    /// <summary>Runs the reusable storage contract checks against the Azure adapter.</summary>
    [Fact(Skip = "Sqrzl does not yet accept an encryption scope in a blob SAS or Copy Blob From URL (sqrzl-emulator#85, #86).")]
    public async Task ShouldPassSharedConformanceGivenAzureBlobEndpoint()
    {
        // Arrange
        // Sqrzl serves Azure Blob under the account name as the first path segment, with the compose credentials.
        var connection = Environment.GetEnvironmentVariable("PORTIA_AZURE_BLOB_TEST_CONNECTION")
            ?? (Environment.GetEnvironmentVariable("PORTIA_S3_TEST_ENDPOINT") is { } endpoint
                ? $"DefaultEndpointsProtocol=http;AccountName=portia-test;AccountKey={Convert.ToBase64String("portia-local-secret"u8)};BlobEndpoint={endpoint.TrimEnd('/')}/portia-test;"
                : throw new InvalidOperationException("Set PORTIA_S3_TEST_ENDPOINT after starting 'docker compose up -d sqrzl'."));
        var scope = Environment.GetEnvironmentVariable("PORTIA_AZURE_BLOB_TEST_ENCRYPTION_SCOPE") ?? "portia-conformance";
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var firstTenant = new TenantId("conformance-a");
        var secondTenant = new TenantId("conformance-b");
        var locations = new Dictionary<string, ObjectStorageLocation>(StringComparer.Ordinal)
        {
            [firstTenant.Value] = new($"portia-a-{suffix}", scope),
            [secondTenant.Value] = new($"portia-b-{suffix}", scope)
        };
        var service = new BlobServiceClient(connection);
        var services = new ServiceCollection();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<IObjectStorageTenantResolver>(new LocationResolver(locations));
        services.AddAzureObjectStorage(builder => builder.UseClient(service));
        using var host = services.BuildServiceProvider();
        using var transfer = new HttpClient();
        var storage = host.GetRequiredService<IObjectStorage>();
        foreach (var location in locations.Values)
        {
            await service.CreateBlobContainerAsync(location.BucketReference);
        }

        // Act
        var run = async () => await ObjectStorageConformance.VerifyAsync(storage, firstTenant, secondTenant,
            async (part, bytes, ct) =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Put, part.UploadUri) { Content = new ByteArrayContent(bytes.ToArray()) };
                using var response = await transfer.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                return new ObjectPartReceipt(part.PartNumber, HttpUtility.ParseQueryString(part.UploadUri.Query)["blockid"]!);
            });

        // Assert
        try
        {
            await run();
        }
        finally
        {
            foreach (var location in locations.Values)
            {
                await service.DeleteBlobContainerAsync(location.BucketReference);
            }
        }
    }

    sealed class LocationResolver(IReadOnlyDictionary<string, ObjectStorageLocation> locations) : IObjectStorageTenantResolver
    {
        public string GetObjectKey(TenantId tenantId, ObjectDigest expected) =>
            $"content/sha256/{expected.Sha256[..2]}/{expected.Sha256}";

        public string GetUploadKey(TenantId tenantId, Uuid uploadId) => $"staging/{uploadId:N}";

        public ValueTask<ObjectStorageLocation> ResolveAsync(TenantId tenantId, CancellationToken ct = default) =>
            ValueTask.FromResult(locations[tenantId.Value]);
    }
}
