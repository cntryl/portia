using System;
using System.IO;
using System.Text.Json;
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
using Cntryl.Portia.Secrets.Aws;
using Xunit;

namespace Cntryl.Portia.Tests.DataProtection;

/// <summary>Exercises AWS key snapshot caching, cancellation, and expiry with deterministic clients.</summary>
public sealed class AwsDataProtectionKeyRingTests
{
    /// <summary>Coalesces concurrent requests for one manifest and root.</summary>
    [Fact]
    public async Task ShouldShareManifestAndKeyUnwrapGivenConcurrentAcquires()
    {
        // Arrange
        var time = new ManualTimeProvider();
        var secrets = new FakeSecrets(Manifest(1));
        var kms = new FakeKms { Block = true };
        using var ring = new AwsDataProtectionKeyRing(new AwsSecretReader(secrets), kms, "secret", "arn:aws:kms:region:account:key/id", "ring", TimeSpan.FromMinutes(5), time);

        // Act
        var first = ring.AcquireActiveAsync(TestContext.Current.CancellationToken).AsTask();
        await kms.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = ring.AcquireActiveAsync(TestContext.Current.CancellationToken).AsTask();
        kms.Release.TrySetResult(true);
        using var firstLease = await first;
        using var secondLease = await second;

        // Assert
        Assert.Equal(1, secrets.Calls);
        Assert.Equal(1, kms.Calls);
        Assert.True(firstLease.IsActive);
        Assert.True(secondLease.IsActive);
        Assert.Equal(firstLease.RootKey.ToArray(), secondLease.RootKey.ToArray());
        Assert.All(kms.LastPlaintextBuffer!, value => Assert.Equal(0, value));
    }

    /// <summary>Refuses a KMS response that arrives after its snapshot expired.</summary>
    [Fact]
    public async Task ShouldRejectLateKmsResultGivenExpiredSnapshot()
    {
        // Arrange
        var time = new ManualTimeProvider();
        var secrets = new FakeSecrets(Manifest(1));
        var kms = new FakeKms { Block = true };
        using var ring = new AwsDataProtectionKeyRing(new AwsSecretReader(secrets), kms, "secret", "arn:aws:kms:region:account:key/id", "ring", TimeSpan.FromSeconds(5), time);
        var acquisition = ring.AcquireActiveAsync(TestContext.Current.CancellationToken).AsTask();
        await kms.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Act
        time.Advance(TimeSpan.FromSeconds(5));
        kms.Release.TrySetResult(true);
        var exception = await Record.ExceptionAsync(async () => await acquisition);

        // Assert
        Assert.IsType<TimeoutException>(exception);
        Assert.Equal(1, secrets.Calls);
        Assert.Equal(1, kms.Calls);
    }

    /// <summary>Enforces the snapshot deadline even when the expiry timer has yet to run.</summary>
    [Fact]
    public async Task ShouldRejectCachedRootGivenDeadlinePassesBeforeLeasing()
    {
        // Arrange
        using var time = new ManualTimeProvider();
        using var secrets = new FakeSecrets(Manifest(1));
        using var kms = new FakeKms();
        using var ring = new AwsDataProtectionKeyRing(new AwsSecretReader(secrets), kms, "secret", "arn:aws:kms:region:account:key/id", "ring", TimeSpan.FromSeconds(5), time);
        var cancellationToken = TestContext.Current.CancellationToken;
        using var warmLease = await ring.AcquireActiveAsync(cancellationToken);
        time.AdvanceAfterNextRead(TimeSpan.FromSeconds(5));

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            using var lease = await ring.AcquireActiveAsync(cancellationToken);
        });

        // Assert
        Assert.IsType<TimeoutException>(exception);
        Assert.Equal(1, secrets.Calls);
        Assert.Equal(1, kms.Calls);
    }

    /// <summary>Keeps the shared unwrap alive when one caller cancels.</summary>
    [Fact]
    public async Task ShouldPreserveSharedUnwrapGivenCancelledWaiter()
    {
        // Arrange
        var time = new ManualTimeProvider();
        var secrets = new FakeSecrets(Manifest(1));
        var kms = new FakeKms { Block = true };
        using var ring = new AwsDataProtectionKeyRing(new AwsSecretReader(secrets), kms, "secret", "arn:aws:kms:region:account:key/id", "ring", TimeSpan.FromMinutes(5), time);
        using var cancellation = new CancellationTokenSource();
        var canceledWaiter = ring.AcquireActiveAsync(cancellation.Token).AsTask();
        await kms.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var survivor = ring.AcquireActiveAsync(TestContext.Current.CancellationToken).AsTask();

        // Act
        cancellation.Cancel();
        var cancellationException = await Record.ExceptionAsync(async () => await canceledWaiter);
        kms.Release.TrySetResult(true);
        using var lease = await survivor;

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(cancellationException);
        Assert.True(lease.IsActive);
        Assert.Equal(1, secrets.Calls);
        Assert.Equal(1, kms.Calls);
    }

    /// <summary>Rejects a lower manifest revision after cache expiry.</summary>
    [Fact]
    public async Task ShouldRejectManifestGivenRevisionDowngrade()
    {
        // Arrange
        var time = new ManualTimeProvider();
        var secrets = new FakeSecrets(Manifest(2), Manifest(1));
        var kms = new FakeKms();
        using var ring = new AwsDataProtectionKeyRing(new AwsSecretReader(secrets), kms, "secret", "arn:aws:kms:region:account:key/id", "ring", TimeSpan.FromSeconds(1), time);
        using (var lease = await ring.AcquireActiveAsync(TestContext.Current.CancellationToken))
        { }

        // Act
        time.Advance(TimeSpan.FromSeconds(1));
        var exception = await Record.ExceptionAsync(async () => await ring.AcquireActiveAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("move backwards", exception!.Message, StringComparison.Ordinal);
    }

    /// <summary>Retries an unwrap failure without rereading the manifest.</summary>
    [Fact]
    public async Task ShouldRetryKeyUnwrapGivenTransientKmsFailure()
    {
        // Arrange
        var time = new ManualTimeProvider();
        var secrets = new FakeSecrets(Manifest(1));
        var kms = new FakeKms { FailuresRemaining = 1 };
        using var ring = new AwsDataProtectionKeyRing(new AwsSecretReader(secrets), kms, "secret", "arn:aws:kms:region:account:key/id", "ring", TimeSpan.FromMinutes(5), time);

        // Act
        var firstException = await Record.ExceptionAsync(async () => await ring.AcquireActiveAsync(TestContext.Current.CancellationToken));
        using var lease = await ring.AcquireActiveAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.IsType<InvalidOperationException>(firstException);
        Assert.True(lease.IsActive);
        Assert.Equal(1, secrets.Calls);
        Assert.Equal(2, kms.Calls);
    }

    /// <summary>Rejects a revision reused for changed secret content.</summary>
    [Fact]
    public async Task ShouldRejectManifestGivenReusedRevisionWithChangedContent()
    {
        // Arrange
        var time = new ManualTimeProvider();
        var secrets = new FakeSecrets(Manifest(1), Manifest(1, wrappedRoot: 8));
        var kms = new FakeKms();
        using var ring = new AwsDataProtectionKeyRing(new AwsSecretReader(secrets), kms, "secret", "arn:aws:kms:region:account:key/id", "ring", TimeSpan.FromSeconds(1), time);
        using (var lease = await ring.AcquireActiveAsync(TestContext.Current.CancellationToken))
        { }

        // Act
        time.Advance(TimeSpan.FromSeconds(1));
        var exception = await Record.ExceptionAsync(async () => await ring.AcquireActiveAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("revision was reused", exception!.Message, StringComparison.Ordinal);
    }

    static string Manifest(long revision, byte wrappedRoot = 7) => JsonSerializer.Serialize(new
    {
        version = 1,
        ringId = "ring",
        revision,
        activeKeyId = "protection-1",
        keys = new[]
        {
            new { keyId = "protection-1", role = "protection", wrappedRoot = Convert.ToBase64String(new[] { wrappedRoot, (byte)8, (byte)9 }) },
            new { keyId = "index-1", role = "index", wrappedRoot = Convert.ToBase64String(new byte[] { 4, 5, 6 }) },
        },
    });

    sealed class FakeSecrets(params string[] manifests) : AmazonSecretsManagerClient(new AnonymousAWSCredentials(), RegionEndpoint.USEast1)
    {
        int _index;
        public int Calls;
        public override Task<GetSecretValueResponse> GetSecretValueAsync(GetSecretValueRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, manifests.Length - 1);
            return Task.FromResult(new GetSecretValueResponse { SecretString = manifests[index], VersionId = "00000000000000000000000000000001" });
        }
    }

    sealed class FakeKms : AmazonKeyManagementServiceClient
    {
        public readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Block;
        public int FailuresRemaining;
        public int Calls;
        public byte[]? LastPlaintextBuffer;

        public FakeKms() : base(new AnonymousAWSCredentials(), RegionEndpoint.USEast1) { }

        public override async Task<DecryptResponse> DecryptAsync(DecryptRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult(true);

            if (Block)
                await Release.Task;

            if (FailuresRemaining > 0)
            {
                FailuresRemaining--;
                throw new InvalidOperationException("Simulated KMS refusal.");
            }

            var plaintext = new MemoryStream();
            plaintext.Write(new byte[32]);
            plaintext.Position = 0;
            plaintext.TryGetBuffer(out var buffer);
            LastPlaintextBuffer = buffer.Array;
            return new DecryptResponse
            {
                KeyId = request.KeyId,
                Plaintext = plaintext,
            };
        }
    }

    sealed class ManualTimeProvider : TimeProvider, IDisposable
    {
        long _ticks;
        long _advanceAfterRead;
        ManualTimer? _timer;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            var current = Interlocked.Read(ref _ticks);
            Interlocked.Add(ref _ticks, Interlocked.Exchange(ref _advanceAfterRead, 0));
            return current;
        }

        public void AdvanceAfterNextRead(TimeSpan amount) => Interlocked.Exchange(ref _advanceAfterRead, amount.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timer = new ManualTimer(this, callback, state, dueTime);
            return _timer;
        }
        public void Dispose() => _timer?.Dispose();
        public void Advance(TimeSpan amount)
        {
            Interlocked.Add(ref _ticks, amount.Ticks);
            _timer?.FireIfDue();
        }
        sealed class ManualTimer(ManualTimeProvider time, TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            readonly long _expiresAt = time.GetTimestamp() + due.Ticks;
            int _fired;
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Interlocked.Exchange(ref _fired, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
            public void FireIfDue()
            {
                if (time.GetTimestamp() >= _expiresAt && Interlocked.Exchange(ref _fired, 1) == 0)
                    callback(state);
            }
        }
    }
}
