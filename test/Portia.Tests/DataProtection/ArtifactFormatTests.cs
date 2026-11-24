using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cntryl.Portia.DataProtection;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Cntryl.Portia.Tests.DataProtection;

public sealed class ArtifactFormatTests
{
    static readonly byte[] Value = "synthetic value"u8.ToArray();
    static readonly byte[] Context = "tenant/42/field"u8.ToArray();
    const string Purpose = "example";

    // Fixed vectors produced independently with Node's crypto module.
    [Fact]
    public async Task ShouldDecryptGivenVersionOnePayloadVector()
    {
        // Arrange
        using var services = CreateServices();
        var protector = services.GetRequiredService<IPayloadProtector>();
        var artifact = Convert.FromHexString(
            "50445001010161000102030405060708090a0b5888da654e4685c2e326f359ad4b61ec7675bd85a457a69dce5f9989649d89");

        // Act
        using var opened = await protector.UnprotectAsync(artifact, Purpose, Context, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("a", opened.KeyId);
        Assert.Equal(Value, opened.Plaintext.ToArray());
        Assert.False(opened.NeedsRotation);
    }

    [Fact]
    public async Task ShouldProduceExpectedBytesGivenVersionOneVerificationVector()
    {
        // Arrange
        using var services = CreateServices();
        var hasher = services.GetRequiredService<IKeyedHasher>();
        var expected = Convert.FromHexString("504450010201614361dfe8b4ccb538921251d80c1da6f307a1e0ae38d18128195a190154ebce9a");

        // Act
        var hash = await hasher.CreateVerificationHashAsync(Value, Purpose, Context, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expected, hash);
    }

    [Fact]
    public async Task ShouldProduceExpectedBytesGivenVersionOneIndexVector()
    {
        // Arrange
        using var services = CreateServices();
        var hasher = services.GetRequiredService<IKeyedHasher>();
        var expected = Convert.FromHexString("504450010305696e646578a87b4841092422bb18eb25fd828808eb640e46142fbdf527fc5d9cbc6e5db0a0");

        // Act
        var hash = await hasher.ComputeIndexHashAsync(Value, Purpose, Context, "index", TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expected, hash);
    }

    static ServiceProvider CreateServices() => new ServiceCollection()
        .AddSingleton<IDataProtectionKeyRing, VectorRing>()
        .AddPortiaDataProtection()
        .BuildServiceProvider();

    sealed class VectorRing : IDataProtectionKeyRing
    {
        static readonly byte[] Protection = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        static readonly byte[] Index = Enumerable.Range(32, 32).Select(value => (byte)value).ToArray();

        public ValueTask<DataProtectionKeyLease> AcquireActiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DataProtectionKeyLease("a", DataProtectionKeyRole.Protection, true, Protection));

        public ValueTask<DataProtectionKeyLease?> AcquireAsync(
            string keyId,
            DataProtectionKeyRole role,
            CancellationToken cancellationToken
        ) =>
            ValueTask.FromResult<DataProtectionKeyLease?>((keyId, role) switch
            {
                ("a", DataProtectionKeyRole.Protection) => new(keyId, role, true, Protection),
                ("index", DataProtectionKeyRole.Index) => new(keyId, role, false, Index),
                _ => null,
            });
    }
}
