using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.DataProtection;

/// <summary>Composes Portia data protection services.</summary>
public static class PortiaDataProtectionServiceCollectionExtensions
{
    /// <summary>Registers payload protection and keyed hashing against the configured key ring.</summary>
    public static IServiceCollection AddPortiaDataProtection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<DataProtectionCrypto>(static provider => new(DataProtectionKeyRingResolver.GetExactlyOne(provider)));
        services.TryAddSingleton<IPayloadProtector>(static provider => provider.GetRequiredService<DataProtectionCrypto>());
        services.TryAddSingleton<IKeyedHasher>(static provider => provider.GetRequiredService<DataProtectionCrypto>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DataProtectionStartupValidator>());
        return services;
    }
}

sealed class DataProtectionStartupValidator(IServiceProvider services) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Validate(cancellationToken);
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    async Task Validate(CancellationToken cancellationToken)
    {
        var ring = DataProtectionKeyRingResolver.GetExactlyOne(services);
        using var lease = await ring.AcquireActiveAsync(cancellationToken).ConfigureAwait(false);

        if (lease.Role != DataProtectionKeyRole.Protection || !lease.IsActive)
            throw new InvalidOperationException("The data protection key ring must provide exactly one active protection key.");
    }
}

static class DataProtectionKeyRingResolver
{
    internal static IDataProtectionKeyRing GetExactlyOne(IServiceProvider services)
    {
        var rings = services.GetServices<IDataProtectionKeyRing>().ToArray();

        if (rings.Length != 1)
            throw new InvalidOperationException("Exactly one data protection key ring must be registered.");

        return rings[0];
    }
}
