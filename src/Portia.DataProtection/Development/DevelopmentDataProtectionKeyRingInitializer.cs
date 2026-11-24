using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.DataProtection;

/// <summary>Creates and rotates a local development key-ring file.</summary>
public static class DevelopmentDataProtectionKeyRingInitializer
{
    /// <summary>The stable identifier for the independently provisioned development index key.</summary>
    public const string DevelopmentIndexKeyId = "development-index-1";

    /// <summary>Creates a new ring if absent. Existing key material is never overwritten.</summary>
    public static void Initialize(string absolutePath, IHostEnvironment environment)
    {
        ValidatePathAndEnvironment(absolutePath, environment);
        var directory = Path.GetDirectoryName(absolutePath)!;
        var directoryExisted = Directory.Exists(directory);

        if (directoryExisted)
            ValidateDirectoryPermissions(directory);
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else
            throw new PlatformNotSupportedException("The development data protection key ring supports macOS and Linux only.");

        ValidateDirectoryPermissions(directory);


        var protection = RandomNumberGenerator.GetBytes(32);
        var index = RandomNumberGenerator.GetBytes(32);
        var document = new DevelopmentDataProtectionKeyRing.RingDocument("development-protection-1",
            [new DevelopmentDataProtectionKeyRing.RingKey("development-protection-1", protection)],
            [new DevelopmentDataProtectionKeyRing.RingKey(DevelopmentIndexKeyId, index)]);

        try
        {
            using var gate = AcquireRotationGate(absolutePath);
            DevelopmentDataProtectionKeyRing.WriteAtomically(absolutePath, document, overwrite: false);
        }
        finally
        {
            document.Dispose();
        }
    }

    /// <summary>Atomically rotates the active development protection key while retaining old keys for reads.</summary>
    public static void RotateProtectionKey(string absolutePath, IHostEnvironment environment)
    {
        ValidatePathAndEnvironment(absolutePath, environment);
        ValidateDirectoryPermissions(Path.GetDirectoryName(absolutePath)!);
        using var gate = AcquireRotationGate(absolutePath);
        using var document = DevelopmentDataProtectionKeyRing.ReadDocument(absolutePath);

        if (document.ProtectionKeys.Length >= 64)
            throw new InvalidOperationException("The development key ring reached its 64-key retention limit; provision a new ring before rotating again.");


        var key = RandomNumberGenerator.GetBytes(32);
        var id = $"dev-protection-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}";
        var keys = new DevelopmentDataProtectionKeyRing.RingKey[document.ProtectionKeys.Length + 1];
        keys[0] = new DevelopmentDataProtectionKeyRing.RingKey(id, key);
        Array.Copy(document.ProtectionKeys, 0, keys, 1, document.ProtectionKeys.Length);
        var updated = new DevelopmentDataProtectionKeyRing.RingDocument(id, keys, document.IndexKeys);

        try
        {
            DevelopmentDataProtectionKeyRing.WriteAtomically(absolutePath, updated, overwrite: true);
        }
        finally
        {
            updated.Dispose();
        }
    }

    static FileStream AcquireRotationGate(string path)
    {
        var lockPath = $"{path}.lock";
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.ReadWrite,
            Options = FileOptions.WriteThrough,
        };

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;


        var stream = new FileStream(lockPath, options);

        try
        {
            var lockMode = OperatingSystem.IsLinux() ? File.GetUnixFileMode(lockPath) : OperatingSystem.IsMacOS()
                ? File.GetUnixFileMode(lockPath)
                : throw new PlatformNotSupportedException("The development data protection key ring supports macOS and Linux only.");

            if (lockMode != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
                throw new UnauthorizedAccessException("The development key ring rotation lock must have mode 0600.");



            if (NativeMethods.Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), NativeMethods.LockExclusive) != 0)
                throw new IOException("Could not obtain the development key ring rotation lock.");

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    static void ValidatePathAndEnvironment(string absolutePath, IHostEnvironment environment)
    {
        DevelopmentDataProtectionKeyRing.EnsureSupportedPlatform();
        ArgumentNullException.ThrowIfNull(environment);

        if (!environment.IsDevelopment())
            throw new InvalidOperationException("Development key ring initialization is allowed only in the Development environment.");

        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        if (!Path.IsPathFullyQualified(absolutePath))
            throw new ArgumentException("The development key ring path must be absolute.", nameof(absolutePath));
    }

    internal static void ValidateDirectoryPermissions(string path)
    {
        var mode = OperatingSystem.IsLinux() ? File.GetUnixFileMode(path) : OperatingSystem.IsMacOS()
            ? File.GetUnixFileMode(path)
            : throw new PlatformNotSupportedException("The development data protection key ring supports macOS and Linux only.");

        if (mode != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
            throw new UnauthorizedAccessException("The development data protection key ring parent directory must have mode 0700.");
    }

    static partial class NativeMethods
    {
        internal const int LockExclusive = 2;

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "flock", SetLastError = true)]
        internal static extern int Flock(int fileDescriptor, int operation);
    }
}
