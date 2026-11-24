using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Cntryl.Portia.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.DataProtection.Aws;

/// <summary>Registers an AWS Secrets Manager and KMS backed Portia key ring.</summary>
public static class AwsDataProtectionKeyRingRegistration
{
    /// <summary>Registers the AWS key ring. Add Portia's payload services separately.</summary>
    public static IServiceCollection AddAwsDataProtectionKeyRing(this IServiceCollection services,
        IAmazonSecretsManager secretsManager, IAmazonKeyManagementService keyManagementService,
        string secretArn, string wrappingKeyArn, string ringId, TimeSpan? cacheLifetime = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(secretsManager);
        ArgumentNullException.ThrowIfNull(keyManagementService);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretArn);
        ArgumentException.ThrowIfNullOrWhiteSpace(wrappingKeyArn);
        ArgumentException.ThrowIfNullOrWhiteSpace(ringId);
        var lifetime = cacheLifetime ?? TimeSpan.FromMinutes(5);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(cacheLifetime), "Cache lifetime must be positive and no more than one hour.");
        services.AddSingleton<IDataProtectionKeyRing>(_ => new AwsDataProtectionKeyRing(
            secretsManager, keyManagementService, secretArn, wrappingKeyArn, ringId, lifetime, TimeProvider.System));
        return services;
    }
}

sealed class AwsDataProtectionKeyRing : IDataProtectionKeyRing, IDisposable
{
    const int MaxManifestBytes = 1_048_576;
    const int MaxKeys = 128;
    static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    readonly IAmazonSecretsManager _secrets;
    readonly IAmazonKeyManagementService _kms;
    readonly string _secretArn, _wrappingKeyArn, _ringId;
    readonly TimeSpan _lifetime;
    readonly TimeProvider _time;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly CancellationTokenSource _shutdown = new();
    ITimer? _expiryTimer;
    Snapshot? _snapshot;
    Task<Snapshot>? _load;
    long _generation;
    int _disposed;

    public AwsDataProtectionKeyRing(IAmazonSecretsManager secrets, IAmazonKeyManagementService kms,
        string secretArn, string wrappingKeyArn, string ringId, TimeSpan lifetime, TimeProvider time)
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
        Snapshot snapshot;
        Task<Snapshot> load;
        await _gate.WaitAsync(callerToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_snapshot is { } cached && _time.GetTimestamp() < cached.ExpiresAt)
                snapshot = cached;
            else
            {
                ExpireSnapshot();
                load = _load ??= LoadSnapshotAsync(Interlocked.Increment(ref _generation));
                snapshot = null!;
                goto AwaitLoad;
            }
        }
        finally { _gate.Release(); }
        return CreateLease(snapshot, keyId, role, active);

    AwaitLoad:
        var loaded = await load.WaitAsync(callerToken).ConfigureAwait(false);
        callerToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(callerToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!ReferenceEquals(_load, load))
                throw new InvalidOperationException("The key snapshot changed while it was loading.");
            if (_time.GetTimestamp() >= loaded.ExpiresAt)
            {
                _load = null;
                loaded.Dispose();
                throw new TimeoutException("The AWS key snapshot expired before it could be leased.");
            }
            _snapshot = loaded;
            _load = null;
            _expiryTimer?.Dispose();
            _expiryTimer = _time.CreateTimer(static s => ((AwsDataProtectionKeyRing)s!).ExpireFromTimer(), this, _lifetime, Timeout.InfiniteTimeSpan);
            snapshot = loaded;
        }
        finally { _gate.Release(); }
        return CreateLease(snapshot, keyId, role, active);
    }

    static DataProtectionKeyLease? CreateLease(Snapshot snapshot, string? keyId, DataProtectionKeyRole role, bool active)
    {
        if (active)
            keyId = snapshot.ActiveKeyId;
        if (!snapshot.Roots.TryGetValue((keyId!, role), out var root))
            return null;
        return new DataProtectionKeyLease(keyId!, role, active, root);
    }

    async Task<Snapshot> LoadSnapshotAsync(long generation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(OperationTimeout);
        byte[]? manifestBytes = null;
        try
        {
            var response = await _secrets.GetSecretValueAsync(new GetSecretValueRequest { SecretId = _secretArn, VersionStage = "AWSCURRENT" }, timeout.Token).ConfigureAwait(false);
            if (response.SecretString is null)
                throw new InvalidOperationException("AWS key manifest must be stored as SecretString.");
            manifestBytes = System.Text.Encoding.UTF8.GetBytes(response.SecretString);
            if (manifestBytes.Length > MaxManifestBytes)
                throw new InvalidOperationException("AWS key manifest exceeds the supported size.");
            using var doc = JsonDocument.Parse(manifestBytes);
            var root = doc.RootElement;
            RequireObjectProperties(root, "version", "ringId", "revision", "activeKeyId", "keys");
            if (root.GetProperty("version").GetInt32() != 1)
                throw new InvalidOperationException("Unsupported AWS key manifest version.");
            if (!StringComparer.Ordinal.Equals(root.GetProperty("ringId").GetString(), _ringId))
                throw new InvalidOperationException("AWS key manifest ringId does not match the configured ring.");
            if (root.GetProperty("revision").GetInt64() <= 0)
                throw new InvalidOperationException("AWS key manifest revision must be positive.");
            var activeId = root.GetProperty("activeKeyId").GetString();
            ValidateId(activeId, "activeKeyId");
            var keys = root.GetProperty("keys");
            if (keys.ValueKind != JsonValueKind.Array || keys.GetArrayLength() == 0 || keys.GetArrayLength() > MaxKeys)
                throw new InvalidOperationException("AWS key manifest key count is invalid.");
            var roots = new Dictionary<(string, DataProtectionKeyRole), byte[]>();
            foreach (var item in keys.EnumerateArray())
            {
                RequireObjectProperties(item, "keyId", "role", "wrappedRoot");
                var id = item.GetProperty("keyId").GetString();
                ValidateId(id, "keyId");
                var roleText = item.GetProperty("role").GetString();
                var role = roleText switch { "protection" => DataProtectionKeyRole.Protection, "index" => DataProtectionKeyRole.Index, _ => throw new InvalidOperationException("AWS key manifest role is invalid.") };
                if (role == DataProtectionKeyRole.Index && StringComparer.Ordinal.Equals(id, activeId))
                    throw new InvalidOperationException("The active key must have protection role.");
                var wrapped = Convert.FromBase64String(item.GetProperty("wrappedRoot").GetString() ?? "");
                if (wrapped.Length is < 1 or > 65536)
                    throw new InvalidOperationException("Wrapped root size is invalid.");
                var context = new Dictionary<string, string> { ["portia:ring"] = _ringId, ["portia:key"] = id!, ["portia:role"] = roleText! };
                var result = await _kms.DecryptAsync(new DecryptRequest { CiphertextBlob = new MemoryStream(wrapped, writable: false), KeyId = _wrappingKeyArn, EncryptionContext = context }, timeout.Token).ConfigureAwait(false);
                CryptographicOperations.ZeroMemory(wrapped);
                if (!StringComparer.Ordinal.Equals(result.KeyId, _wrappingKeyArn))
                    throw new InvalidOperationException("AWS KMS used an unexpected wrapping key ARN.");
                var clear = result.Plaintext.ToArray();
                if (clear.Length != 32)
                { CryptographicOperations.ZeroMemory(clear); throw new InvalidOperationException("Decrypted data protection root must contain 32 bytes."); }
                if (!roots.TryAdd((id!, role), clear))
                { CryptographicOperations.ZeroMemory(clear); throw new InvalidOperationException("AWS key manifest contains duplicate keys."); }
            }
            if (!roots.ContainsKey((activeId!, DataProtectionKeyRole.Protection)))
                throw new InvalidOperationException("The active protection key is absent from the manifest.");
            return new Snapshot(activeId!, roots, checked(_time.GetTimestamp() + (long)(_lifetime.TotalSeconds * _time.TimestampFrequency)));
        }
        catch
        {
            throw;
        }
        finally { if (manifestBytes is not null) CryptographicOperations.ZeroMemory(manifestBytes); }
    }

    static void ValidateId(string? value, string field)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Contains(':') || value.Contains('\0')) throw new InvalidOperationException($"AWS key manifest {field} is invalid."); }
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
    void ExpireFromTimer() { if (Interlocked.Exchange(ref _snapshot, null) is { } snapshot) snapshot.Dispose(); }
    void ExpireSnapshot() { if (_snapshot is { } s) { _snapshot = null; s.Dispose(); } _expiryTimer?.Dispose(); _expiryTimer = null; }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _shutdown.Cancel();
        _expiryTimer?.Dispose();
        ExpireSnapshot();
        _gate.Dispose();
        _shutdown.Dispose();
    }

    sealed class Snapshot(string activeKeyId, Dictionary<(string, DataProtectionKeyRole), byte[]> roots, long expiresAt) : IDisposable
    {
        public string ActiveKeyId { get; } = activeKeyId; public Dictionary<(string, DataProtectionKeyRole), byte[]> Roots { get; } = roots; public long ExpiresAt { get; } = expiresAt;
        public void Dispose() { foreach (var root in Roots.Values) CryptographicOperations.ZeroMemory(root); Roots.Clear(); }
    }
}
