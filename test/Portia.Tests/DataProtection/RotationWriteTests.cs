using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cntryl.Portia.DataProtection;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Cntryl.Portia.Tests.DataProtection;

/// <summary>Exercises conditional application writes during key rotation.</summary>
public sealed class RotationWriteTests
{
    /// <summary>Identifies a write prepared under the prior active key.</summary>
    [Fact]
    public async Task ShouldFlagLateWriteGivenWriterPreparedBeforeActivation()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var ring = new RotationRing();
        using var services = new ServiceCollection().AddSingleton<IDataProtectionKeyRing>(ring)
            .AddPortiaDataProtection().BuildServiceProvider();
        var protector = services.GetRequiredService<IPayloadProtector>();
        var context = "tenant-7/customer-42/tax-id"u8.ToArray();
        var beforeActivation = await protector.ProtectAsync("in-flight"u8.ToArray(), "tax-id", context, token);
        ring.ActiveId = "b";

        // Act
        using var afterCommit = await protector.UnprotectAsync(beforeActivation, "tax-id", context, token);

        // Assert
        Assert.Equal("a", afterCommit.KeyId);
        Assert.True(afterCommit.NeedsRotation);
    }

    /// <summary>Preserves a newer value when the rotation update loses the write race.</summary>
    [Fact]
    public async Task ShouldPreserveNewerWriteGivenRotationLosesConditionalUpdate()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var ring = new RotationRing();
        using var services = new ServiceCollection().AddSingleton<IDataProtectionKeyRing>(ring)
            .AddPortiaDataProtection().BuildServiceProvider();
        var protector = services.GetRequiredService<IPayloadProtector>();
        var context = "tenant-7/customer-42/tax-id"u8.ToArray();
        var old = new StoredPayload(await protector.ProtectAsync("old"u8.ToArray(), "tax-id", context, token));
        ring.ActiveId = "b";
        var edited = new StoredPayload(await protector.ProtectAsync("edited"u8.ToArray(), "tax-id", context, token));
        var current = edited;
        using var previous = await protector.UnprotectAsync(old.Value, "tax-id", context, token);

        // Act
        var updated = await ReprotectIfNeeded(old, expected => ReferenceEquals(Interlocked.CompareExchange(ref current,
            expected.Replacement, expected.Original), expected.Original), protector, context, token);

        // Assert
        Assert.True(previous.NeedsRotation);
        Assert.False(updated);
        Assert.Same(edited, current);
        using var readback = await protector.UnprotectAsync(current.Value, "tax-id", context, token);
        Assert.Equal("edited"u8.ToArray(), readback.Plaintext.ToArray());
        Assert.False(readback.NeedsRotation);
    }

    /// <summary>Checks that retrying an already rotated record performs no write.</summary>
    [Fact]
    public async Task ShouldSkipWriteGivenAlreadyRotatedRecord()
    {
        // Arrange
        var token = TestContext.Current.CancellationToken;
        var ring = new RotationRing { ActiveId = "b" };
        using var services = new ServiceCollection().AddSingleton<IDataProtectionKeyRing>(ring)
            .AddPortiaDataProtection().BuildServiceProvider();
        var protector = services.GetRequiredService<IPayloadProtector>();
        var context = "tenant-7/customer-42/tax-id"u8.ToArray();
        var current = new StoredPayload(await protector.ProtectAsync("current"u8.ToArray(), "tax-id", context, token));
        var writes = 0;

        // Act
        var updated = await ReprotectIfNeeded(current, _ =>
        {
            writes++;
            return true;
        }, protector, context, token);

        // Assert
        Assert.False(updated);
        Assert.Equal(0, writes);
    }

    static async Task<bool> ReprotectIfNeeded(
        StoredPayload original,
        Func<(StoredPayload Original, StoredPayload Replacement), bool> compareExchange,
        IPayloadProtector protector,
        ReadOnlyMemory<byte> context,
        CancellationToken cancellationToken
    )
    {
        using var value = await protector.UnprotectAsync(original.Value, "tax-id", context, cancellationToken);

        if (!value.NeedsRotation)
            return false;

        var replacement = await protector.ProtectAsync(value.Plaintext, "tax-id", context, cancellationToken);
        return compareExchange((original, new StoredPayload(replacement)));
    }

    sealed record StoredPayload(byte[] Value);

    sealed class RotationRing : IDataProtectionKeyRing
    {
        readonly byte[] _first = Enumerable.Repeat((byte)1, 32).ToArray();
        readonly byte[] _second = Enumerable.Repeat((byte)2, 32).ToArray();

        public string ActiveId { get; set; } = "a";

        public ValueTask<DataProtectionKeyLease> AcquireActiveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Lease(ActiveId));
        }

        public ValueTask<DataProtectionKeyLease?> AcquireAsync(
            string keyId,
            DataProtectionKeyRole role,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(role == DataProtectionKeyRole.Protection && keyId is "a" or "b"
                ? Lease(keyId)
                : null);
        }

        DataProtectionKeyLease Lease(string keyId) =>
            new(keyId, DataProtectionKeyRole.Protection, keyId == ActiveId, keyId == "a" ? _first : _second);
    }
}
