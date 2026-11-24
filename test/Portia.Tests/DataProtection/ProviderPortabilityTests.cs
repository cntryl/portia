using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Amazon;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

using Cntryl.Portia.DataProtection;
using Cntryl.Portia.DataProtection.Aws;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Cntryl.Portia.Tests.DataProtection;

/// <summary>Exercises the same artifacts across independent key-ring providers.</summary>
public sealed class ProviderPortabilityTests
{
    const string SecretArn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:portia-test-AbCdEf";
    const string WrappingKeyArn = "arn:aws:kms:us-east-1:123456789012:key/12345678-1234-1234-1234-123456789012";
    static readonly byte[] ProtectionRoot = Enumerable.Repeat((byte)0x12, 32).ToArray();
    static readonly byte[] IndexRoot = Enumerable.Repeat((byte)0x34, 32).ToArray();

    /// <summary>Reads ciphertext, verification hashes, and indexes with the same roots and IDs.</summary>
    [Fact]
    public async Task ShouldReadAwsArtifactsGivenIndependentProviderWithSameKeys()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        using var secrets = new FixtureSecrets();
        using var kms = new FixtureKms();
        using var aws = new ServiceCollection()
            .AddAwsDataProtectionKeyRing(secrets, kms, SecretArn, WrappingKeyArn, "portia-fixture")
            .AddPortiaDataProtection()
            .BuildServiceProvider();
        using var other = new ServiceCollection()
            .AddSingleton<IDataProtectionKeyRing, FixtureRing>()
            .AddPortiaDataProtection()
            .BuildServiceProvider();
        var context = "tenant-1/record-1"u8.ToArray();
        var data = "synthetic-value"u8.ToArray();
        var awsHasher = aws.GetRequiredService<IKeyedHasher>();
        var otherHasher = other.GetRequiredService<IKeyedHasher>();
        var awsProtector = aws.GetRequiredService<IPayloadProtector>();
        var otherProtector = other.GetRequiredService<IPayloadProtector>();
        var payload = await awsProtector.ProtectAsync(data, "sample.payload", context, token);
        var hash = await awsHasher.CreateVerificationHashAsync(data, "sample.verify", context, token);
        var index = await awsHasher.ComputeIndexHashAsync(data, "sample.index", context, "index-a", token);

        // Act
        using var opened = await otherProtector.UnprotectAsync(payload, "sample.payload", context, token);
        var verified = await otherHasher.VerifyAsync(hash, data, "sample.verify", context, token);
        var otherIndex = await otherHasher.ComputeIndexHashAsync(data, "sample.index", context, "index-a", token);

        // Assert
        Assert.Equal(data, opened.Plaintext.ToArray());
        Assert.True(verified);
        Assert.Equal(index, otherIndex);
    }

    sealed class FixtureSecrets() : AmazonSecretsManagerClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        public override Task<GetSecretValueResponse> GetSecretValueAsync(
            GetSecretValueRequest request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(new GetSecretValueResponse
            {
                ARN = SecretArn,
                VersionId = "11111111111111111111111111111111",
                SecretString = """
                    {"version":1,"ringId":"portia-fixture","revision":1,"activeKeyId":"a","keys":[
                    {"keyId":"a","role":"protection","wrappedRoot":"AQ=="},
                    {"keyId":"index-a","role":"index","wrappedRoot":"Ag=="}]}
                    """,
                VersionStages = ["AWSCURRENT"],
            });
    }

    sealed class FixtureKms() : AmazonKeyManagementServiceClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        public override Task<DecryptResponse> DecryptAsync(DecryptRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new DecryptResponse
            {
                KeyId = WrappingKeyArn,
                EncryptionAlgorithm = EncryptionAlgorithmSpec.SYMMETRIC_DEFAULT,
                Plaintext = new MemoryStream((request.EncryptionContext["portia:key"] == "a" ? ProtectionRoot : IndexRoot).ToArray()),
            });
    }

    sealed class FixtureRing : IDataProtectionKeyRing
    {
        public ValueTask<DataProtectionKeyLease> AcquireActiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DataProtectionKeyLease("a", DataProtectionKeyRole.Protection, true, ProtectionRoot));

        public ValueTask<DataProtectionKeyLease?> AcquireAsync(
            string keyId,
            DataProtectionKeyRole role,
            CancellationToken cancellationToken
        ) =>
            ValueTask.FromResult<DataProtectionKeyLease?>((keyId, role) switch
            {
                ("a", DataProtectionKeyRole.Protection) => new(keyId, role, true, ProtectionRoot),
                ("index-a", DataProtectionKeyRole.Index) => new(keyId, role, false, IndexRoot),
                _ => null,
            });
    }
}
