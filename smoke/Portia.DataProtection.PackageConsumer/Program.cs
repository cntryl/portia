using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Cntryl.Portia.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var environment = new DevelopmentHostEnvironment();

if (args.Length > 0)
{
    var path = GetExplicitPath();
    var commandEnvironment = Host.CreateApplicationBuilder(args).Environment;

    if (args.Length != 1)
        throw new ArgumentException("Use exactly one command: init or rotate.");

    switch (args[0])
    {
        case "init":
            DevelopmentDataProtectionKeyRingInitializer.Initialize(path, commandEnvironment);
            return;
        case "rotate":
            DevelopmentDataProtectionKeyRingInitializer.RotateProtectionKey(path, commandEnvironment);
            return;
        default:
            throw new ArgumentException("Use exactly one command: init or rotate.");
    }
}

var root = Directory.CreateTempSubdirectory("portia-data-protection-consumer-").FullName;
var ringPath = Path.Combine(root, "key-ring", "keys.json");

try
{
    DevelopmentDataProtectionKeyRingInitializer.Initialize(ringPath, environment);
    using var firstHost = BuildHost(ringPath, environment);
    await firstHost.StartAsync();
    byte[] payload;
    byte[] verification;
    byte[] pinnedIndex;
    string originalKeyId;

    try
    {
        var protector = firstHost.Services.GetRequiredService<IPayloadProtector>();
        var hasher = firstHost.Services.GetRequiredService<IKeyedHasher>();
        var ring = firstHost.Services.GetRequiredService<IDataProtectionKeyRing>();
        var value = Encoding.UTF8.GetBytes("locally protected value");
        var context = Encoding.UTF8.GetBytes("customer:example");
        payload = await protector.ProtectAsync(value, "consumer-example", context);
        verification = await hasher.CreateVerificationHashAsync(value, "consumer-example", context);
        pinnedIndex = await hasher.ComputeIndexHashAsync(value, "customer-index", context,
            DevelopmentDataProtectionKeyRingInitializer.DevelopmentIndexKeyId);
        using var active = await ring.AcquireActiveAsync();
        originalKeyId = active.KeyId;
    }
    finally
    {
        await firstHost.StopAsync();
    }

    DevelopmentDataProtectionKeyRingInitializer.RotateProtectionKey(ringPath, environment);
    using var restartedHost = BuildHost(ringPath, environment);
    await restartedHost.StartAsync();

    try
    {
        var protector = restartedHost.Services.GetRequiredService<IPayloadProtector>();
        var hasher = restartedHost.Services.GetRequiredService<IKeyedHasher>();
        var ring = restartedHost.Services.GetRequiredService<IDataProtectionKeyRing>();
        var value = Encoding.UTF8.GetBytes("locally protected value");
        var context = Encoding.UTF8.GetBytes("customer:example");
        using var recovered = await protector.UnprotectAsync(payload, "consumer-example", context);
        var verified = await hasher.VerifyAsync(verification, value, "consumer-example", context);
        var indexAfterRotation = await hasher.ComputeIndexHashAsync(value, "customer-index", context,
            DevelopmentDataProtectionKeyRingInitializer.DevelopmentIndexKeyId);
        using var active = await ring.AcquireActiveAsync();
        var replacement = await protector.ProtectAsync(recovered.Plaintext, "consumer-example", context);
        using var reprotected = await protector.UnprotectAsync(replacement, "consumer-example", context);

        if (!recovered.NeedsRotation || !verified || active.KeyId == originalKeyId || reprotected.NeedsRotation ||
            !reprotected.Plaintext.Span.SequenceEqual(value) ||
            !CryptographicOperations.FixedTimeEquals(pinnedIndex, indexAfterRotation))
            throw new InvalidOperationException("The development data protection consumer exercise failed.");

        Console.WriteLine("Development data protection restart, rotation, verification, and pinned-index checks passed.");
    }
    finally
    {
        await restartedHost.StopAsync();
        CryptographicOperations.ZeroMemory(payload);
        CryptographicOperations.ZeroMemory(verification);
        CryptographicOperations.ZeroMemory(pinnedIndex);
    }
}
finally
{
    Directory.Delete(root, true);
}

static IHost BuildHost(string path, IHostEnvironment environment)
{
    var builder = Host.CreateApplicationBuilder();
    builder.Environment.EnvironmentName = environment.EnvironmentName;
    builder.Services.AddDevelopmentDataProtectionKeyRing(path);
    builder.Services.AddPortiaDataProtection();
    return builder.Build();
}

static string GetExplicitPath()
{
    var path = Environment.GetEnvironmentVariable("PORTIA_DEVELOPMENT_KEY_RING");

    if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        throw new InvalidOperationException("Set PORTIA_DEVELOPMENT_KEY_RING to an absolute key-ring file path for init or rotate.");

    return path;
}

sealed class DevelopmentHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "Portia.DataProtection.PackageConsumer";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
}
