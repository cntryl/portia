namespace Cntryl.Portia.Testing;

/// <summary>An in-memory implementation for exercising tenant-scoped object storage behavior.</summary>
public sealed class FakeObjectStorage : IObjectStorage
{
    readonly object _sync = new();
    readonly Dictionary<TenantId, Dictionary<string, byte[]>> _objects = [];
    readonly Dictionary<string, Upload> _uploads = new(StringComparer.Ordinal);
    readonly Dictionary<string, CompletedUpload> _completedUploads = new(StringComparer.Ordinal);
    DateTimeOffset _nextCompletedPrune;

    /// <summary>Gets the number of active upload sessions.</summary>
    public int ActiveUploadCount
    {
        get
        {
            lock (_sync)
            {
                return _uploads.Count;
            }
        }
    }

    /// <summary>Starts an isolated upload for the supplied tenant and content digest.</summary>
    public ValueTask<ObjectUploadSession> CreateUploadAsync(TenantId tenantId, ObjectDigest expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ct.ThrowIfCancellationRequested();
        var token = Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            PruneCompletedUploads();
            _uploads.Add(token, new Upload(tenantId, expected, DateTimeOffset.UtcNow.AddHours(24)));
        }

        return ValueTask.FromResult(new ObjectUploadSession(token));
    }

    /// <summary>Returns an in-memory upload destination.</summary>
    public ValueTask<ObjectUploadPart> SignPartAsync(ObjectUploadSession session, int partNumber, TimeSpan lifetime, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (partNumber is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(partNumber));
        }

        if (lifetime <= TimeSpan.Zero || lifetime > ObjectStorageLimits.MaximumDownloadLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        lock (_sync)
        {
            var upload = GetUpload(session);
            EnsureActive(upload);
            var expires = Min(DateTimeOffset.UtcNow.Add(lifetime), upload.ExpiresAt);
            return ValueTask.FromResult(new ObjectUploadPart(partNumber, new Uri($"memory://upload/{session.Token}/{partNumber}"), expires));
        }
    }

    /// <summary>Accepts the bytes addressed by a previously signed fake part.</summary>
    public async ValueTask<ObjectPartReceipt> AcceptPartAsync(ObjectUploadSession session, int partNumber, Stream contents, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contents);
        ct.ThrowIfCancellationRequested();
        if (partNumber is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(partNumber));
        }

        lock (_sync)
        {
            EnsureActive(GetUpload(session));
        }

        using var memory = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await contents.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > ObjectStorageLimits.MaximumObjectLength)
            {
                throw new InvalidDataException("Part exceeds the maximum object length.");
            }

            await memory.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        var receipt = Guid.NewGuid().ToString("N");
        var content = memory.ToArray();
        lock (_sync)
        {
            var upload = GetUpload(session);
            EnsureActive(upload);
            upload.Parts[partNumber] = (receipt, content);
            return new ObjectPartReceipt(partNumber, receipt);
        }
    }

    /// <inheritdoc />
    public ValueTask CompleteUploadAsync(ObjectUploadSession session, IReadOnlyList<ObjectPartReceipt> parts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(parts);
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            PruneCompletedUploads();
            if (_completedUploads.TryGetValue(session.Token, out var completed))
            {
                if (completed.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    _completedUploads.Remove(session.Token);
                    throw new InvalidOperationException("The upload session has expired.");
                }

                if (!SameReceipts(parts, completed.Receipts))
                {
                    throw new InvalidDataException("Part receipts do not match the completed upload.");
                }

                if (!_objects.TryGetValue(completed.TenantId, out var objects) ||
                    !objects.TryGetValue(completed.Expected.Sha256, out var existing))
                {
                    throw new FileNotFoundException("The completed object was not found for this tenant.");
                }

                if (existing.LongLength != completed.Expected.Length ||
                    !string.Equals(Convert.ToHexString(SHA256.HashData(existing)).ToLowerInvariant(),
                        completed.Expected.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The completed object failed verification.");
                }

                return ValueTask.CompletedTask;
            }

            var upload = GetUpload(session);
            EnsureActive(upload);
            try
            {
                if (parts.Count is < 1 or > 10_000 || parts.Any(static part => part is null || part.PartNumber is < 1 or > 10_000) ||
                    parts.Select(static part => part.PartNumber).Distinct().Count() != parts.Count ||
                    parts.Any(part => !upload.Parts.TryGetValue(part.PartNumber, out var stored) || stored.Receipt != part.Token))
                {
                    throw new InvalidDataException("Part receipts are missing, duplicated, or invalid.");
                }

                using var output = new MemoryStream();
                var ordered = parts.OrderBy(static part => part.PartNumber).ToArray();
                for (var index = 0; index < ordered.Length; index++)
                {
                    var bytes = upload.Parts[ordered[index].PartNumber].Content;
                    if (index < ordered.Length - 1 && bytes.Length < ObjectStorageLimits.MinimumNonfinalPartLength)
                    {
                        throw new InvalidDataException("Every nonfinal multipart part must contain at least 5 MiB.");
                    }

                    if (output.Length + bytes.LongLength > ObjectStorageLimits.MaximumObjectLength ||
                        output.Length + bytes.LongLength > upload.Expected.Length)
                    {
                        throw new InvalidDataException("Uploaded object exceeds its declared or maximum length.");
                    }

                    output.Write(bytes);
                }

                var content = output.ToArray();
                var digest = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
                if (content.LongLength != upload.Expected.Length || digest != upload.Expected.Sha256)
                {
                    throw new InvalidDataException("Uploaded object length or SHA-256 does not match the declared content.");
                }

                var tenantObjects = GetObjects(upload.TenantId);
                if (tenantObjects.TryGetValue(digest, out var existing))
                {
                    if (existing.LongLength != content.LongLength ||
                        !CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(content)))
                    {
                        throw new InvalidDataException("An object already exists at the immutable content key but failed verification.");
                    }
                }
                else
                {
                    tenantObjects.Add(digest, content);
                }

                _completedUploads.Add(session.Token, new CompletedUpload(upload.TenantId, upload.Expected, upload.ExpiresAt,
                    ordered.Select(static part => new ObjectPartReceipt(part.PartNumber, part.Token)).ToArray()));
                return ValueTask.CompletedTask;
            }
            finally
            {
                _uploads.Remove(session.Token);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask AbortUploadAsync(ObjectUploadSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_sync)
        {
            _uploads.Remove(session.Token);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> HeadAndVerifyAsync(TenantId tenantId, ObjectDigest expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!GetObjects(tenantId).TryGetValue(expected.Sha256, out var content))
            {
                return ValueTask.FromResult(false);
            }

            var digest = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            return ValueTask.FromResult(content.LongLength == expected.Length && digest == expected.Sha256);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ObjectDownload> CreateDownloadAsync(TenantId tenantId, ObjectDigest expected, TimeSpan lifetime, CancellationToken ct = default)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > ObjectStorageLimits.MaximumDownloadLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        if (!await HeadAndVerifyAsync(tenantId, expected, ct).ConfigureAwait(false))
        {
            throw new FileNotFoundException("The verified object was not found.");
        }

        var expires = DateTimeOffset.UtcNow.Add(lifetime);
        return new ObjectDownload(new Uri($"memory://download/{Uri.EscapeDataString(tenantId.Value)}/{expected.Sha256}"), expires);
    }

    /// <inheritdoc />
    public ValueTask<Stream?> OpenReadAsync(TenantId tenantId, ObjectDigest expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!GetObjects(tenantId).TryGetValue(expected.Sha256, out var content) ||
                content.LongLength != expected.Length || !string.Equals(
                    Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), expected.Sha256, StringComparison.Ordinal))
            {
                return ValueTask.FromResult<Stream?>(null);
            }

            return ValueTask.FromResult<Stream?>(new MemoryStream(content, writable: false));
        }
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(TenantId tenantId, ObjectDigest expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            GetObjects(tenantId).Remove(expected.Sha256);
        }

        return ValueTask.CompletedTask;
    }

    Upload GetUpload(ObjectUploadSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _uploads.TryGetValue(session.Token, out var upload)
            ? upload
            : throw new InvalidOperationException("The upload session is unknown, completed, or aborted.");
    }

    Dictionary<string, byte[]> GetObjects(TenantId tenantId)
    {
        if (!_objects.TryGetValue(tenantId, out var objects))
        {
            objects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            _objects.Add(tenantId, objects);
        }

        return objects;
    }

    static void EnsureActive(Upload upload)
    {
        if (upload.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("The upload session has expired.");
        }
    }

    static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left < right ? left : right;

    void PruneCompletedUploads()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextCompletedPrune)
        {
            return;
        }

        foreach (var token in _completedUploads.Where(pair => pair.Value.ExpiresAt <= now).Select(static pair => pair.Key).ToArray())
        {
            _completedUploads.Remove(token);
        }

        _nextCompletedPrune = now.AddMinutes(1);
    }

    static bool SameReceipts(IReadOnlyList<ObjectPartReceipt> actual, ObjectPartReceipt[] expected)
    {
        if (actual.Count != expected.Length || actual.Any(static part => part is null))
        {
            return false;
        }

        var ordered = actual.OrderBy(static part => part.PartNumber).ToArray();
        return ordered.SequenceEqual(expected);
    }

    sealed record Upload(TenantId TenantId, ObjectDigest Expected, DateTimeOffset ExpiresAt)
    {
        public Dictionary<int, (string Receipt, byte[] Content)> Parts { get; } = [];
    }

    sealed record CompletedUpload(TenantId TenantId, ObjectDigest Expected, DateTimeOffset ExpiresAt,
        ObjectPartReceipt[] Receipts);
}
