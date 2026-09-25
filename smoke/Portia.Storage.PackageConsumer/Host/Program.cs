using System.Reflection;
using StorageSmoke.Contracts;

using var s3 = new AmazonS3Client(
    new BasicAWSCredentials("smoke-access", "smoke-secret"),
    new AmazonS3Config { ServiceURL = "http://127.0.0.1:9000", ForcePathStyle = true });
var builder = Host.CreateApplicationBuilder();
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddSingleton<IObjectStorageTenantResolver, SmokeTenantResolver>();
builder.Services.AddAwsObjectStorage(options => options.UseClient(s3));
using var host = builder.Build();
_ = host.Services.GetRequiredService<IObjectStorage>();

var contractReferencesAws = typeof(AssetReference).Assembly.GetReferencedAssemblies()
    .Any(reference => reference.Name?.StartsWith("AWSSDK.", StringComparison.Ordinal) == true);
if (contractReferencesAws)
    throw new InvalidOperationException("Application contracts unexpectedly reference an AWS SDK assembly.");

Console.WriteLine("Storage abstraction and separate AWS host package consumer passed.");

sealed class SmokeTenantResolver : IObjectStorageTenantResolver
{
    public ValueTask<ObjectStorageLocation> ResolveAsync(Cntryl.Portia.TenantId tenantId, System.Threading.CancellationToken ct = default)
        => ValueTask.FromResult(new ObjectStorageLocation("smoke-bucket", "alias/smoke-key"));
}
