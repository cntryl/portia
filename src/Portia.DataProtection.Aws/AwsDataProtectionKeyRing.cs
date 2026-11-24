using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Cntryl.Portia.DataProtection;
using Cntryl.Portia.Secrets;

namespace Cntryl.Portia.DataProtection.Aws;

sealed class AwsDataProtectionKeyRing : IDataProtectionKeyRing, IDisposable
{
    const int MaxManifestBytes = 1_048_576;
    const int MaxKeys = 128;
    static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    readonly ISecretReader _secrets;
    readonly IAmazonKeyManagementService _kms;
    readonly string _secretArn, _wrappingKeyArn, _ringId;
    readonly TimeSpan _lifetime;
    readonly TimeProvider _time;
    readonly object _sync = new();
    readonly CancellationTokenSource _shutdown = new();
    ITimer? _expiryTimer;
    Snapshot? _snapshot;
    Task<Snapshot>? _load;
    long _lastRevision;
    string? _lastVersionId;
    string? _lastManifestDigest;
    int _disposed;

    public AwsDataProtectionKeyRing(
        ISecretReader secrets,
        IAmazonKeyManagementService kms,
        string secretArn,
        string wrappingKeyArn,
        string ringId,
        TimeSpan lifetime,
        TimeProvider time
    )
    { _secrets = secrets; _kms = kms; _secretArn = secretArn; _wrappingKeyArn = wrappingKeyArn; _ringId = ringId; _lifetime = lifetime; _time = time; }

    public async ValueTask<DataProtectionKeyLease> AcquireActiveAsync(CancellationToken cancellationToken = default) => (await AcquireCoreAsync(null, DataProtectionKeyRole.Protection, true, cancellationToken).ConfigureAwait(false))!;
    public ValueTask<DataProtectionKeyLease?> AcquireAsync(string keyId, DataProtectionKeyRole role, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        if (role is not (DataProtectionKeyRole.Protection or DataProtectionKeyRole.Index))
            throw new ArgumentOutOfRangeException(nameof(role));

        return AcquireCoreAsync(keyId, role, false, cancellationToken);
    }

    async ValueTask<DataProtectionKeyLease?> AcquireCoreAsync(string? keyId, DataProtectionKeyRole role, bool active, CancellationToken callerToken)
    {
        callerToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var snapshot = await GetSnapshotAsync(callerToken).ConfigureAwait(false);
        return await CreateLeaseAsync(snapshot, keyId, role, active, callerToken).ConfigureAwait(false);
    }

    async Task<Snapshot> GetSnapshotAsync(CancellationToken callerToken)
    {
        Task<Snapshot> load;
        TaskCompletionSource<Snapshot>? producer = null;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);

            if (_snapshot is { } cached && _time.GetTimestamp() < cached.ExpiresAt)
                return cached;

            ExpireSnapshotLocked();

            if (_load is null)
            {
                producer = new TaskCompletionSource<Snapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                _load = producer.Task;
            }

            load = _load;
        }

        if (producer is not null)
            _ = LoadAndPublishAsync(producer);

        return await load.WaitAsync(callerToken).ConfigureAwait(false);
    }

    async Task LoadAndPublishAsync(TaskCompletionSource<Snapshot> producer)
    {
        try
        {
            var snapshot = await LoadSnapshotAsync().ConfigureAwait(false);
            lock (_sync)
            {
                if (_disposed != 0 || !ReferenceEquals(_load, producer.Task))
                {
                    snapshot.Dispose();
                    throw new ObjectDisposedException(nameof(AwsDataProtectionKeyRing));
                }

                _snapshot = snapshot;
                _load = null;
                _expiryTimer?.Dispose();
                _expiryTimer = _time.CreateTimer(static state =>
                {
                    var (ring, expected) = ((AwsDataProtectionKeyRing, Snapshot))state!;
                    ring.Expire(expected);
                }, (this, snapshot), _lifetime, Timeout.InfiniteTimeSpan);
            }
            producer.TrySetResult(snapshot);
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_load, producer.Task))
                    _load = null;
            }
            producer.TrySetException(exception);
        }
    }

    void Expire(Snapshot expected)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_snapshot, expected))
                ExpireSnapshotLocked();
        }
    }

    async ValueTask<DataProtectionKeyLease?> CreateLeaseAsync(Snapshot snapshot, string? keyId, DataProtectionKeyRole role, bool active, CancellationToken callerToken)
    {
        if (active)
            keyId = snapshot.ActiveKeyId;

        if (!snapshot.Entries.TryGetValue((keyId!, role), out var entry))
        {
            lock (snapshot.Sync)
            {
                callerToken.ThrowIfCancellationRequested();
                snapshot.ValidateLeaseDeadline(_time);
                return null;
            }
        }

        return await entry.GetLeaseAsync(snapshot, _kms, _wrappingKeyArn, _ringId, keyId!, role,
            role == DataProtectionKeyRole.Protection && keyId == snapshot.ActiveKeyId,
            _time, OperationTimeout, callerToken).ConfigureAwait(false);
    }

    async Task<Snapshot> LoadSnapshotAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(OperationTimeout);
        Dictionary<(string, DataProtectionKeyRole), KeyEntry>? entries = null;

        try
        {
            using var manifest = await _secrets.ReadCurrentAsync(_secretArn, timeout.Token).ConfigureAwait(false);
            var manifestBytes = manifest.Bytes;

            if (manifestBytes.Length > MaxManifestBytes)
                throw new InvalidOperationException("AWS key manifest exceeds the supported size.");

            using var doc = JsonDocument.Parse(manifestBytes);
            var root = doc.RootElement;
            RequireObjectProperties(root, "version", "ringId", "revision", "activeKeyId", "keys");

            if (root.GetProperty("version").GetInt32() != 1)
                throw new InvalidOperationException("Unsupported AWS key manifest version.");

            if (!StringComparer.Ordinal.Equals(root.GetProperty("ringId").GetString(), _ringId))
                throw new InvalidOperationException("AWS key manifest ringId does not match the configured ring.");

            var revision = root.GetProperty("revision").GetInt64();

            if (revision <= 0)
                throw new InvalidOperationException("AWS key manifest revision must be positive.");


            if (revision < Volatile.Read(ref _lastRevision))
                throw new InvalidOperationException("AWS key manifest revision cannot move backwards.");

            var manifestDigest = Convert.ToHexString(SHA256.HashData(manifestBytes.Span));

            var activeId = root.GetProperty("activeKeyId").GetString();
            ValidateId(activeId, "activeKeyId");
            var keys = root.GetProperty("keys");

            if (keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() == 0 || keys.GetArrayLength() > MaxKeys)
                throw new InvalidOperationException("AWS key manifest key count is invalid.");

            entries = [];
            var ids = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in keys.EnumerateArray())
            {
                RequireObjectProperties(item, "keyId", "role", "wrappedRoot");
                var id = item.GetProperty("keyId").GetString();
                ValidateId(id, "keyId");

                if (!ids.Add(id!))
                    throw new InvalidOperationException("AWS key manifest contains duplicate key IDs.");

                var roleText = item.GetProperty("role").GetString();
                var role = roleText switch { "protection" => DataProtectionKeyRole.Protection, "index" => DataProtectionKeyRole.Index, _ => throw new InvalidOperationException("AWS key manifest role is invalid.") };

                if (role == DataProtectionKeyRole.Index && StringComparer.Ordinal.Equals(id, activeId))
                    throw new InvalidOperationException("The active key must have protection role.");

                var wrapped = Convert.FromBase64String(item.GetProperty("wrappedRoot").GetString() ?? "");

                if (wrapped.Length is < 1 or > 65536)
                    throw new InvalidOperationException("Wrapped root size is invalid.");

                if (!entries.TryAdd((id!, role), new KeyEntry(id!, wrapped, roleText!)))
                {
                    CryptographicOperations.ZeroMemory(wrapped);
                    throw new InvalidOperationException("AWS key manifest contains duplicate keys.");
                }
            }

            if (!entries.ContainsKey((activeId!, DataProtectionKeyRole.Protection)))
                throw new InvalidOperationException("The active protection key is absent from the manifest.");

            lock (_sync)
            {
                if (revision == _lastRevision &&
                    (!StringComparer.Ordinal.Equals(manifest.VersionId, _lastVersionId) ||
                     !StringComparer.Ordinal.Equals(manifestDigest, _lastManifestDigest)))
                    throw new InvalidOperationException("AWS key manifest revision was reused for different content or a different Secrets Manager version.");

                _lastRevision = revision;
                _lastVersionId = manifest.VersionId;
                _lastManifestDigest = manifestDigest;
            }

            return new Snapshot(activeId!, entries, checked(_time.GetTimestamp() + (long)(_lifetime.TotalSeconds * _time.TimestampFrequency)), _shutdown.Token);
        }
        catch
        {
            if (entries is not null)
            {
                foreach (var entry in entries.Values)
                    entry.Dispose();
            }

            throw;
        }
    }

    static void ValidateId(string? value, string field)
    {
        if (!DataProtectionKeyIds.IsValid(value))
            throw new InvalidOperationException($"AWS key manifest {field} is invalid.");
    }

    static void RequireObjectProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("AWS key manifest shape is invalid.");

        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in element.EnumerateObject())
            if (!names.Add(p.Name))
                throw new InvalidOperationException("AWS key manifest contains duplicate properties.");

        if (names.Count != expected.Length || Array.Exists(expected, e => !names.Contains(e)))
            throw new InvalidOperationException("AWS key manifest contains missing or unknown properties.");
    }
    void ExpireSnapshotLocked()
    {
        if (_snapshot is { } s)
        {
            _snapshot = null;
            s.Dispose();
        }

        _expiryTimer?.Dispose();
        _expiryTimer = null;
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _shutdown.Cancel();
            _expiryTimer?.Dispose();
            ExpireSnapshotLocked();
        }
    }

    sealed class Snapshot(string activeKeyId, Dictionary<(string, DataProtectionKeyRole), KeyEntry> entries, long expiresAt, CancellationToken shutdownToken) : IDisposable
    {
        readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        readonly object _sync = new();
        bool _disposed;
        public string ActiveKeyId { get; } = activeKeyId;
        public Dictionary<(string, DataProtectionKeyRole), KeyEntry> Entries { get; } = entries;
        public long ExpiresAt { get; } = expiresAt;
        public CancellationToken LifetimeToken => _lifetime.Token;
        public object Sync => _sync;
        public bool IsDisposed { get { lock (_sync) return _disposed; } }

        public void ValidateLeaseDeadline(TimeProvider time)
        {
            if (time.GetTimestamp() >= ExpiresAt)
                throw new TimeoutException("AWS key snapshot expired before a lease could be created.");

            ObjectDisposedException.ThrowIf(IsDisposed, this);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _lifetime.Cancel();

                foreach (var entry in Entries.Values)
                    entry.Dispose();

            }

            _lifetime.Dispose();
        }
    }

    sealed class KeyEntry(string keyId, byte[] wrapped, string roleText) : IDisposable
    {
        readonly string _keyId = keyId;
        readonly byte[] _wrapped = wrapped;
        readonly string _roleText = roleText;
        Task<byte[]>? _rootLoad;
        byte[]? _root;

        public async Task<DataProtectionKeyLease> GetLeaseAsync(
            Snapshot snapshot,
            IAmazonKeyManagementService kms,
            string keyArn,
            string ringId,
            string leaseKeyId,
            DataProtectionKeyRole role,
            bool active,
            TimeProvider time,
            TimeSpan timeout,
            CancellationToken callerToken
        )
        {
            Task<byte[]> load;
            TaskCompletionSource<byte[]>? producer = null;
            lock (snapshot.Sync)
            {
                callerToken.ThrowIfCancellationRequested();
                snapshot.ValidateLeaseDeadline(time);

                if (_root is not null)
                    return new DataProtectionKeyLease(leaseKeyId, role, active, _root);


                if (_rootLoad is null)
                {
                    producer = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _rootLoad = producer.Task;
                }

                load = _rootLoad;
            }

            if (producer is not null)
                _ = UnwrapAndPublishAsync(snapshot, kms, keyArn, ringId, time, timeout, producer);

            await load.WaitAsync(callerToken).ConfigureAwait(false);

            lock (snapshot.Sync)
            {
                callerToken.ThrowIfCancellationRequested();
                snapshot.ValidateLeaseDeadline(time);

                if (_root is null)
                    throw new DataProtectionKeyUnavailableException();

                return new DataProtectionKeyLease(leaseKeyId, role, active, _root);
            }
        }

        async Task UnwrapAndPublishAsync(
            Snapshot snapshot,
            IAmazonKeyManagementService kms,
            string keyArn,
            string ringId,
            TimeProvider time,
            TimeSpan timeout,
            TaskCompletionSource<byte[]> producer
        )
        {
            try
            {
                var root = await UnwrapAsync(snapshot, kms, keyArn, ringId, time, timeout).ConfigureAwait(false);
                producer.TrySetResult(root);
            }
            catch (Exception exception)
            {
                lock (snapshot.Sync)
                {
                    if (ReferenceEquals(_rootLoad, producer.Task))
                        _rootLoad = null;
                }
                producer.TrySetException(exception);
            }
        }

        async Task<byte[]> UnwrapAsync(Snapshot snapshot, IAmazonKeyManagementService kms, string keyArn, string ringId, TimeProvider time, TimeSpan timeout)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(snapshot.LifetimeToken);
            cts.CancelAfter(timeout);
            var context = new Dictionary<string, string>
            {
                ["portia:ring"] = ringId,
                ["portia:key"] = _keyId,
                ["portia:role"] = _roleText,
            };
            var encrypted = (byte[])_wrapped.Clone();

            try
            {
                var result = await kms.DecryptAsync(new DecryptRequest
                {
                    CiphertextBlob = new MemoryStream(encrypted, writable: false),
                    KeyId = keyArn,
                    EncryptionContext = context,
                }, cts.Token).ConfigureAwait(false);

                byte[] clear;
                using (result.Plaintext)
                {
                    try
                    {
                        clear = result.Plaintext.ToArray();
                    }
                    finally
                    {
                        ZeroStream(result.Plaintext);
                    }
                }

                if (!StringComparer.Ordinal.Equals(result.KeyId, keyArn))
                {
                    CryptographicOperations.ZeroMemory(clear);
                    throw new InvalidOperationException("AWS KMS used an unexpected wrapping key ARN.");
                }

                if (clear.Length != 32)
                {
                    CryptographicOperations.ZeroMemory(clear);
                    throw new InvalidOperationException("Decrypted data protection root must contain 32 bytes.");
                }

                lock (snapshot.Sync)
                {
                    if (snapshot.IsDisposed || time.GetTimestamp() >= snapshot.ExpiresAt)
                    {
                        CryptographicOperations.ZeroMemory(clear);
                        throw new TimeoutException("AWS key snapshot expired before key unwrap completed.");
                    }

                    if (cts.IsCancellationRequested)
                    {
                        CryptographicOperations.ZeroMemory(clear);
                        cts.Token.ThrowIfCancellationRequested();
                    }

                    _root = clear;
                    return _root;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }

        static void ZeroStream(MemoryStream stream)
        {
            if (stream.TryGetBuffer(out var buffer))
            {
                CryptographicOperations.ZeroMemory(buffer.AsSpan());
                return;
            }

            if (stream.CanWrite)
            {
                stream.Position = 0;
                Span<byte> zeros = stackalloc byte[256];
                zeros.Clear();

                while (stream.Position < stream.Length)
                {
                    stream.Write(zeros[..(int)Math.Min(zeros.Length, stream.Length - stream.Position)]);
                }
            }
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(_wrapped);

            if (_root is not null)
                CryptographicOperations.ZeroMemory(_root);

            _root = null;
        }
    }
}
