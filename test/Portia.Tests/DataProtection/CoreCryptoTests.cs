using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cntryl.Portia.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cntryl.Portia.Tests.DataProtection;

/// <summary>Exercises the shared data protection crypto contracts.</summary>
public sealed class CoreCryptoTests
{
    /// <summary>Checks that a retained key decrypts and marks rotation.</summary>
    [Fact]
    public async Task ShouldRoundTripPayloadAndReportRotationGivenRetainedKey()
    {
        // Arrange
        var oldRoot = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var currentRoot = Enumerable.Range(0, 32).Select(static value => (byte)(value + 32)).ToArray();
        var ring = new TestRing(new DataProtectionKeyLease("old", DataProtectionKeyRole.Protection, true, oldRoot),
            new DataProtectionKeyLease("current", DataProtectionKeyRole.Protection, false, currentRoot));
        using var provider = BuildProvider(ring);
        var crypto = provider.GetRequiredService<IPayloadProtector>();
        var plaintext = new byte[] { 1, 2, 3, 4 };
        var context = new byte[] { 9, 8 };
        var payload = await crypto.ProtectAsync(plaintext, "test", context, TestContext.Current.CancellationToken);
        ring.ActiveId = "current";

        // Act
        using var decrypted = await crypto.UnprotectAsync(payload, "test", context, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(plaintext, decrypted.Plaintext.ToArray());
        Assert.Equal("old", decrypted.KeyId);
        Assert.True(decrypted.NeedsRotation);
        Assert.Equal(new byte[] { (byte)'P', (byte)'D', (byte)'P', 1, 1 }, payload[..5]);
    }

    /// <summary>Checks authentication failures use a sanitized error.</summary>
    [Fact]
    public async Task ShouldRejectTamperingAndWrongContextGivenAuthenticatedPayload()
    {
        // Arrange
        var ring = new TestRing(new DataProtectionKeyLease("key_1", DataProtectionKeyRole.Protection, true, new byte[32]));
        using var provider = BuildProvider(ring);
        var crypto = provider.GetRequiredService<IPayloadProtector>();
        var payload = await crypto.ProtectAsync(new byte[] { 1, 2 }, "purpose", new byte[] { 7 }, TestContext.Current.CancellationToken);
        var tampered = payload.ToArray();
        tampered[^1] ^= 0xff;

        // Act
        var tamperError = await Record.ExceptionAsync(async () => await crypto.UnprotectAsync(tampered, "purpose", new byte[] { 7 }, TestContext.Current.CancellationToken));
        var contextError = await Record.ExceptionAsync(async () => await crypto.UnprotectAsync(payload, "purpose", new byte[] { 8 }, TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<System.Security.Cryptography.CryptographicException>(tamperError);
        Assert.Equal("The data protection artifact is invalid.", tamperError.Message);
        Assert.IsType<System.Security.Cryptography.CryptographicException>(contextError);
        Assert.Equal("The data protection artifact is invalid.", contextError.Message);
    }

    /// <summary>Checks index calculations stay pinned to the requested root.</summary>
    [Fact]
    public async Task ShouldKeepIndexHashPinnedAndRoleCheckedGivenMultipleRoots()
    {
        // Arrange
        var ring = new TestRing(new DataProtectionKeyLease("active", DataProtectionKeyRole.Protection, true, new byte[32]),
            new DataProtectionKeyLease("index_a", DataProtectionKeyRole.Index, false, Enumerable.Repeat((byte)3, 32).ToArray()),
            new DataProtectionKeyLease("index_b", DataProtectionKeyRole.Index, false, Enumerable.Repeat((byte)4, 32).ToArray()));
        using var provider = BuildProvider(ring);
        var crypto = provider.GetRequiredService<IKeyedHasher>();

        // Act
        var ct = TestContext.Current.CancellationToken;
        var first = await crypto.ComputeIndexHashAsync(new byte[] { 1 }, "identifier", new byte[] { 2 }, "index_a", ct);
        var same = await crypto.ComputeIndexHashAsync(new byte[] { 1 }, "identifier", new byte[] { 2 }, "index_a", ct);
        var rotated = await crypto.ComputeIndexHashAsync(new byte[] { 1 }, "identifier", new byte[] { 2 }, "index_b", ct);
        var missing = await Record.ExceptionAsync(async () => await crypto.ComputeIndexHashAsync(new byte[] { 1 }, "identifier", new byte[] { 2 }, "active", ct));

        // Assert
        Assert.Equal(first, same);
        Assert.NotEqual(first, rotated);
        Assert.IsType<System.Security.Cryptography.CryptographicException>(missing);
        Assert.Equal(new byte[] { (byte)'P', (byte)'D', (byte)'P', 1, 3 }, first[..5]);
    }

    /// <summary>Checks the framed HKDF and HMAC output against a fixed vector.</summary>
    [Fact]
    public async Task ShouldMatchVerificationHashVectorGivenFixedInputs()
    {
        // Arrange
        var root = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var ring = new TestRing(new DataProtectionKeyLease("key", DataProtectionKeyRole.Protection, true, root));
        using var provider = BuildProvider(ring);
        var hasher = provider.GetRequiredService<IKeyedHasher>();

        // Act
        var hash = await hasher.CreateVerificationHashAsync(
            "abc"u8.ToArray(), "email", "ctx"u8.ToArray(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(Convert.FromHexString("5044500102036b657990f34be72ddfc2747761b3843716ce79fdf3e7b6bd4e8947f06efbfd34fdfea9"), hash);
    }

    /// <summary>Reports a provider failure separately from a rejected candidate.</summary>
    [Fact]
    public async Task ShouldDistinguishUnavailableKeyGivenVerificationArtifact()
    {
        // Arrange
        var ring = new TestRing(new DataProtectionKeyLease("key", DataProtectionKeyRole.Protection, true, new byte[32]));
        using var provider = BuildProvider(ring);
        var hasher = provider.GetRequiredService<IKeyedHasher>();
        var context = "challenge"u8.ToArray();
        var candidate = "different-code"u8.ToArray();
        var ct = TestContext.Current.CancellationToken;
        var artifact = await hasher.CreateVerificationHashAsync("issued-code"u8.ToArray(), "verify-email", context, ct);

        // Act
        var matched = await hasher.VerifyAsync(artifact, candidate, "verify-email", context, ct);
        ring.Unavailable = true;
        var outage = await Record.ExceptionAsync(async () =>
            await hasher.VerifyAsync(artifact, candidate, "verify-email", context, ct));

        // Assert
        Assert.False(matched);
        Assert.IsType<DataProtectionKeyUnavailableException>(outage);
    }

    sealed class TestRing(params DataProtectionKeyLease[] leases) : IDataProtectionKeyRing
    {
        readonly DataProtectionKeyLease[] _leases = leases;
        public string ActiveId { get; set; } = leases.Single(static lease => lease.IsActive).KeyId;
        public bool Unavailable { get; set; }

        public ValueTask<DataProtectionKeyLease> AcquireActiveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = _leases.Single(lease => lease.KeyId == ActiveId);
            return ValueTask.FromResult(new DataProtectionKeyLease(source.KeyId, source.Role, true, source.RootKey));
        }

        public ValueTask<DataProtectionKeyLease?> AcquireAsync(
            string keyId,
            DataProtectionKeyRole role,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Unavailable)
                return ValueTask.FromException<DataProtectionKeyLease?>(new IOException("Simulated provider outage."));

            var source = _leases.SingleOrDefault(lease => lease.KeyId == keyId && lease.Role == role);
            return ValueTask.FromResult(source is null ? null : new DataProtectionKeyLease(source.KeyId, source.Role,
                source.Role == DataProtectionKeyRole.Protection && source.KeyId == ActiveId, source.RootKey));
        }
    }

    static ServiceProvider BuildProvider(IDataProtectionKeyRing ring)
    {
        var services = new ServiceCollection();
        services.AddSingleton(ring);
        services.AddPortiaDataProtection();
        return services.BuildServiceProvider();
    }
}
