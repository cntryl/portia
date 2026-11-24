using System;
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
        var info = new FileInfo(path);

        if (info.Length is <= 0 or > MaximumFileBytes)
            throw new InvalidDataException("The development data protection key ring has an invalid size.");


        var buffer = new byte[(int)info.Length];

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            if (stream.Length != buffer.Length)
                throw new InvalidDataException("The development data protection key ring changed while being read.");

            stream.ReadExactly(buffer);
            return Parse(buffer);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The development data protection key ring is malformed.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    internal static void WriteAtomically(string path, RingDocument document)
    {
        EnsureSupportedPlatform();
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

            File.Move(temporaryPath, path, true);
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

    static RingDocument Parse(ReadOnlySpan<byte> data)
    {
        var active = string.Empty;
        var protection = new List<RingKey>();
        var indexes = new List<RingKey>();
        var reader = new Utf8JsonReader(data);

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            if (reader.ValueTextEquals("ActiveProtectionKeyId"))
            {
                reader.Read();
                active = reader.GetString() ?? string.Empty;
            }
            else if (reader.ValueTextEquals("ProtectionKeys"))
            {
                ReadKeys(ref reader, protection);
            }
            else if (reader.ValueTextEquals("IndexKeys"))
            {
                ReadKeys(ref reader, indexes);
            }
        }


        var document = new RingDocument(active, protection.ToArray(), indexes.ToArray());
        ValidateDocument(document);
        return document;
    }

    static void ReadKeys(ref Utf8JsonReader reader, List<RingKey> keys)
    {
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            throw new InvalidDataException("The development data protection key ring key list is invalid.");

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new InvalidDataException("The development data protection key ring key is invalid.");

            var id = string.Empty;
            var root = Array.Empty<byte>();

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                if (reader.ValueTextEquals("Id"))
                {
                    reader.Read();
                    id = reader.GetString() ?? string.Empty;
                }
                else if (reader.ValueTextEquals("Key"))
                {
                    reader.Read();
                    root = reader.GetBytesFromBase64();
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }


            keys.Add(new RingKey(id, root));

            if (keys.Count > MaximumKeys)
                throw new InvalidDataException("The development data protection key ring contains too many keys.");
        }
    }

    internal static byte[] SerializeDocument(RingDocument document)
    {
        using var stream = new MemoryStream();

        try
        {
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("ActiveProtectionKeyId", document.ActiveProtectionKeyId);
                WriteKeys(writer, "ProtectionKeys", document.ProtectionKeys);
                WriteKeys(writer, "IndexKeys", document.IndexKeys);
                writer.WriteEndObject();
            }

            return stream.ToArray();
        }
        finally
        {
            if (stream.TryGetBuffer(out var buffer))
                CryptographicOperations.ZeroMemory(buffer.AsSpan(0, (int)stream.Length));
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

    static void ValidateDocument(RingDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.ActiveProtectionKeyId) || document.ProtectionKeys.Length == 0 || document.IndexKeys.Length == 0)
            throw new InvalidDataException("The development data protection key ring is incomplete.");

        if (!document.ProtectionKeys.Any(key => key.Id == document.ActiveProtectionKeyId))
            throw new InvalidDataException("The active protection key is missing from the key ring.");

        var ids = document.ProtectionKeys.Select(key => key.Id).Concat(document.IndexKeys.Select(key => key.Id)).ToArray();

        if (ids.Any(id => !IsValidKeyId(id)) || !IsValidKeyId(document.ActiveProtectionKeyId) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new InvalidDataException("The development data protection key ring contains invalid or duplicate key IDs.");


        foreach (var key in document.ProtectionKeys.Concat(document.IndexKeys))
        {
            if (key.Root.Length != 32)
                throw new InvalidDataException("A development data protection root must contain 32 bytes.");
        }
    }

    static bool IsValidKeyId(string id) => id.Length is > 0 and <= 64 &&
        id.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    static FileStream CreatePrivateFile(string path, FileMode mode)
    {
        var options = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
            Share = FileShare.None,
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
