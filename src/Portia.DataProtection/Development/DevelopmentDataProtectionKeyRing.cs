using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Cntryl.Portia.DataProtection;

sealed class DevelopmentDataProtectionKeyRing(string path) : IDataProtectionKeyRing
{
    const int MaximumFileBytes = 256 * 1024;
    const int MaximumKeys = 64;

    public ValueTask<DataProtectionKeyLease> AcquireActiveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = ReadDocument(path);
        var key = document.ProtectionKeys.Single(item => item.Id == document.ActiveProtectionKeyId);

        return ValueTask.FromResult(new DataProtectionKeyLease(key.Id, DataProtectionKeyRole.Protection, true, key.Root));
    }

    public ValueTask<DataProtectionKeyLease?> AcquireAsync(string keyId, DataProtectionKeyRole role, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(keyId);
        using var document = ReadDocument(path);
        var keys = role switch
        {
            DataProtectionKeyRole.Protection => document.ProtectionKeys,
            DataProtectionKeyRole.Index => document.IndexKeys,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        var key = keys.SingleOrDefault(item => item.Id == keyId);

        if (key is null)
            return ValueTask.FromResult<DataProtectionKeyLease?>(null);


        return ValueTask.FromResult<DataProtectionKeyLease?>(new DataProtectionKeyLease(key.Id, role,
            role == DataProtectionKeyRole.Protection && key.Id == document.ActiveProtectionKeyId, key.Root));
    }

    internal static RingDocument ReadDocument(string path)
    {
        EnsureSupportedPlatform();

        if (!File.Exists(path))
            throw new FileNotFoundException("The development data protection key ring does not exist. Initialize it explicitly.", path);


        ValidatePermissions(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, bufferSize: 1);

        if (stream.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("The development data protection key ring has an invalid size.");

        var buffer = new byte[(int)stream.Length];

        try
        {
            stream.ReadExactly(buffer);
            return Parse(buffer);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("The development data protection key ring is malformed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    internal static void WriteAtomically(string path, RingDocument document, bool overwrite)
    {
        EnsureSupportedPlatform();

        if (overwrite)
            ValidatePermissions(path);

        var temporaryPath = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var bytes = SerializeDocument(document);

        try
        {
            using (var stream = CreatePrivateFile(temporaryPath, FileMode.CreateNew))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }

            File.Move(temporaryPath, path, overwrite);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);

            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    internal static void EnsureSupportedPlatform()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The development data protection key ring supports macOS and Linux only.");
    }

    static RingDocument Parse(ReadOnlyMemory<byte> data)
    {
        using var json = JsonDocument.Parse(data);
        var root = json.RootElement;
        ValidateProperties(root, "Version", "ActiveProtectionKeyId", "ProtectionKeys", "IndexKeys");

        if (root.GetProperty("Version").GetInt32() != 1)
            throw new InvalidDataException("The development data protection key ring version is unsupported.");

        var active = root.GetProperty("ActiveProtectionKeyId").GetString();

        if (!DataProtectionKeyIds.IsValid(active))
            throw new InvalidDataException("The active development data protection key ID is invalid.");

        var roots = new List<byte[]>();

        try
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var protection = ReadKeys(root.GetProperty("ProtectionKeys"), ids, roots);
            var indexes = ReadKeys(root.GetProperty("IndexKeys"), ids, roots);

            if (!protection.Any(key => key.Id == active))
                throw new InvalidDataException("The active protection key is missing from the key ring.");

            return new RingDocument(active!, protection, indexes);
        }
        catch
        {
            foreach (var bytes in roots)
                CryptographicOperations.ZeroMemory(bytes);

            throw;
        }
    }

    static RingKey[] ReadKeys(JsonElement value, HashSet<string> ids, List<byte[]> roots)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > MaximumKeys)
            throw new InvalidDataException("The development data protection key list is invalid.");

        var keys = new RingKey[value.GetArrayLength()];
        var index = 0;

        foreach (var item in value.EnumerateArray())
        {
            ValidateProperties(item, "Id", "Key");
            var id = item.GetProperty("Id").GetString();

            if (!DataProtectionKeyIds.IsValid(id) || !ids.Add(id!))
                throw new InvalidDataException("The development data protection key ring contains invalid or duplicate key IDs.");

            var root = item.GetProperty("Key").GetBytesFromBase64();
            roots.Add(root);

            if (root.Length != 32)
                throw new InvalidDataException("A development data protection root must contain 32 bytes.");

            keys[index++] = new RingKey(id!, root);
        }

        return keys;
    }

    static void ValidateProperties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The development data protection key ring is malformed.");

        var remaining = new HashSet<string>(expected, StringComparer.Ordinal);

        foreach (var property in value.EnumerateObject())
        {
            if (!remaining.Remove(property.Name))
                throw new InvalidDataException("The development data protection key ring contains an invalid property.");
        }

        if (remaining.Count != 0)
            throw new InvalidDataException("The development data protection key ring is missing a property.");
    }

    static byte[] SerializeDocument(RingDocument document)
    {
        // Allocate room for the largest accepted ring so the buffer never grows while it holds keys.
        var buffer = new ArrayBufferWriter<byte>(24 * 1024);

        try
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("Version", 1);
                writer.WriteString("ActiveProtectionKeyId", document.ActiveProtectionKeyId);
                WriteKeys(writer, "ProtectionKeys", document.ProtectionKeys);
                WriteKeys(writer, "IndexKeys", document.IndexKeys);
                writer.WriteEndObject();
            }

            return buffer.WrittenSpan.ToArray();
        }
        finally
        {
            buffer.Clear();
        }
    }

    static void WriteKeys(Utf8JsonWriter writer, string property, RingKey[] keys)
    {
        writer.WriteStartArray(property);

        foreach (var key in keys)
        {
            writer.WriteStartObject();
            writer.WriteString("Id", key.Id);
            writer.WriteBase64String("Key", key.Root);
            writer.WriteEndObject();
        }


        writer.WriteEndArray();
    }

    static FileStream CreatePrivateFile(string path, FileMode mode)
    {
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 1,
            Options = FileOptions.WriteThrough,
        };

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;


        return new FileStream(path, options);
    }

    static void ValidatePermissions(string path)
    {
        DevelopmentDataProtectionKeyRingInitializer.ValidateDirectoryPermissions(Path.GetDirectoryName(path)!);

        var mode = OperatingSystem.IsLinux() ? File.GetUnixFileMode(path) : OperatingSystem.IsMacOS()
            ? File.GetUnixFileMode(path)
            : throw new PlatformNotSupportedException("The development data protection key ring supports macOS and Linux only.");
        var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        if (mode != expected)
            throw new UnauthorizedAccessException("The development data protection key ring file must have mode 0600.");
    }

    internal sealed class RingDocument(string activeProtectionKeyId, RingKey[] protectionKeys, RingKey[] indexKeys) : IDisposable
    {
        public string ActiveProtectionKeyId { get; } = activeProtectionKeyId;
        public RingKey[] ProtectionKeys { get; } = protectionKeys;
        public RingKey[] IndexKeys { get; } = indexKeys;

        public void Dispose()
        {
            foreach (var key in ProtectionKeys.Concat(IndexKeys))
                CryptographicOperations.ZeroMemory(key.Root);
        }
    }

    internal sealed class RingKey(string id, byte[] root)
    {
        public string Id { get; } = id;
        public byte[] Root { get; } = root;
    }
}
