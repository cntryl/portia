using System.Text.Json;
using Cntryl.Portia.DataProtection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.Tests.DataProtection;

/// <summary>Checks private persistence and lifecycle behavior of the Development key ring.</summary>
public sealed class DevelopmentDataProtectionKeyRingTests
{
    /// <summary>Persists identical protection roots and the stable index key across provider restarts.</summary>
    [Fact]
    public async Task ShouldKeepAnInitializedRingAcrossRestartsGivenPrivateParent()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "secrets", "keys.json");
        var environment = new TestHostEnvironment(Environments.Development);

        // Act
        DevelopmentDataProtectionKeyRingInitializer.Initialize(path, environment);
        var services = BuildProvider(path, environment);
        var active = await services.GetRequiredService<IDataProtectionKeyRing>().AcquireActiveAsync(TestContext.Current.CancellationToken);
        var index = await services.GetRequiredService<IDataProtectionKeyRing>().AcquireAsync(
            DevelopmentDataProtectionKeyRingInitializer.DevelopmentIndexKeyId, DataProtectionKeyRole.Index, TestContext.Current.CancellationToken);
        var restarted = BuildProvider(path, environment);
        var afterRestart = await restarted.GetRequiredService<IDataProtectionKeyRing>().AcquireActiveAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(active.KeyId, afterRestart.KeyId);
        Assert.Equal(active.RootKey.ToArray(), afterRestart.RootKey.ToArray());
        Assert.Equal(DevelopmentDataProtectionKeyRingInitializer.DevelopmentIndexKeyId, index!.KeyId);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        }

        active.Dispose();
        index.Dispose();
        afterRestart.Dispose();
    }

    /// <summary>Does not replace an existing key ring during initialization.</summary>
    [Fact]
    public void ShouldPreserveAnInitializedRingGivenRepeatedInitialization()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "private", "keys.json");
        var environment = new TestHostEnvironment(Environments.Development);
        DevelopmentDataProtectionKeyRingInitializer.Initialize(path, environment);
        var original = File.ReadAllBytes(path);

        // Act
        var exception = Record.Exception(() => DevelopmentDataProtectionKeyRingInitializer.Initialize(path, environment));

        // Assert
        Assert.IsType<IOException>(exception);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    /// <summary>Rejects initialization and rotation outside Development.</summary>
    [Fact]
    public void ShouldRejectHelpersGivenNonDevelopmentEnvironment()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "private", "keys.json");
        var environment = new TestHostEnvironment(Environments.Production);

        // Act
        var initialize = Record.Exception(() => DevelopmentDataProtectionKeyRingInitializer.Initialize(path, environment));
        var rotate = Record.Exception(() => DevelopmentDataProtectionKeyRingInitializer.RotateProtectionKey(path, environment));

        // Assert
        Assert.IsType<InvalidOperationException>(initialize);
        Assert.IsType<InvalidOperationException>(rotate);
        Assert.False(File.Exists(path));
    }

    /// <summary>Rejects resolving the provider outside Development.</summary>
    [Fact]
    public void ShouldRejectRegistrationGivenNonDevelopmentEnvironment()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var services = new ServiceCollection();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Production));
        services.AddDevelopmentDataProtection(Path.Combine(directory.Path, "private", "keys.json"));
        using var provider = services.BuildServiceProvider();

        // Act
        var exception = Record.Exception(() => provider.GetRequiredService<IDataProtectionKeyRing>());

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Rejects key files with group access.</summary>
    [Fact]
    public async Task ShouldRejectAnInsecureFileModeGivenPrivateRing()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "private", "keys.json");
        DevelopmentDataProtectionKeyRingInitializer.Initialize(path, new TestHostEnvironment(Environments.Development));

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var ring = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development))
            .AddDevelopmentDataProtection(path)
            .BuildServiceProvider().GetRequiredService<IDataProtectionKeyRing>();

        // Act
        var exception = await Record.ExceptionAsync(async () => await ring.AcquireActiveAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(exception);
    }

    /// <summary>Retains old protection roots after rotation.</summary>
    [Fact]
    public async Task ShouldRetainOldProtectionKeyGivenRotation()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "private", "keys.json");
        var environment = new TestHostEnvironment(Environments.Development);
        DevelopmentDataProtectionKeyRingInitializer.Initialize(path, environment);
        var ring = BuildProvider(path, environment)
            .GetRequiredService<IDataProtectionKeyRing>();
        using var oldKey = await ring.AcquireActiveAsync(TestContext.Current.CancellationToken);

        // Act
        DevelopmentDataProtectionKeyRingInitializer.RotateProtectionKey(path, environment);
        using var currentKey = await ring.AcquireActiveAsync(TestContext.Current.CancellationToken);
        using var retained = await ring.AcquireAsync(oldKey.KeyId, DataProtectionKeyRole.Protection, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEqual(oldKey.KeyId, currentKey.KeyId);
        Assert.NotNull(retained);
        Assert.False(retained!.IsActive);
        Assert.Equal(oldKey.RootKey.ToArray(), retained.RootKey.ToArray());
    }

    /// <summary>Rejects missing and malformed key ring files.</summary>
    [Fact]
    public async Task ShouldRejectMissingAndMalformedFilesGivenPrivateDirectory()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var privateDirectory = Path.Combine(directory.Path, "private");
        Directory.CreateDirectory(privateDirectory);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(privateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var missing = Path.Combine(privateDirectory, "missing.json");
        var malformed = Path.Combine(privateDirectory, "malformed.json");
        File.WriteAllText(malformed, "{not-json");

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(malformed, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var environment = new TestHostEnvironment(Environments.Development);
        var missingRing = BuildProvider(missing, environment)
            .GetRequiredService<IDataProtectionKeyRing>();
        var malformedRing = BuildProvider(malformed, environment)
            .GetRequiredService<IDataProtectionKeyRing>();

        // Act
        var missingError = await Record.ExceptionAsync(async () => await missingRing.AcquireActiveAsync(TestContext.Current.CancellationToken));
        var malformedError = await Record.ExceptionAsync(async () => await malformedRing.AcquireActiveAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<FileNotFoundException>(missingError);
        Assert.IsType<InvalidDataException>(malformedError);
    }

    /// <summary>Rejects duplicate key identifiers while loading a persisted ring.</summary>
    [Fact]
    public async Task ShouldRejectDuplicateKeyIdentifiersGivenMalformedRing()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "private", "keys.json");
        DevelopmentDataProtectionKeyRingInitializer.Initialize(path, new TestHostEnvironment(Environments.Development));
        var json = File.ReadAllText(path);
        using var parsed = JsonDocument.Parse(json);
        var protectionKey = parsed.RootElement.GetProperty("ProtectionKeys")[0].GetRawText();
        File.WriteAllText(path, json.Replace("],\"IndexKeys\"", $",{protectionKey}],\"IndexKeys\"", StringComparison.Ordinal));
        var ring = BuildProvider(path, new TestHostEnvironment(Environments.Development))
            .GetRequiredService<IDataProtectionKeyRing>();

        // Act
        var exception = await Record.ExceptionAsync(async () => await ring.AcquireActiveAsync(TestContext.Current.CancellationToken));

        // Assert
        Assert.IsType<InvalidDataException>(exception);
    }

    sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "DevelopmentDataProtectionKeyRingTests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    static ServiceProvider BuildProvider(string path, IHostEnvironment environment) => new ServiceCollection()
        .AddSingleton<IHostEnvironment>(environment)
        .AddDevelopmentDataProtection(path)
        .BuildServiceProvider();

    sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}
