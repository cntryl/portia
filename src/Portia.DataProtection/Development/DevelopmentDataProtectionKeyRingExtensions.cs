using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia.DataProtection;

/// <summary>Registers the file-backed key ring intended for local development.</summary>
public static class DevelopmentDataProtectionKeyRingExtensions
{
    /// <summary>Registers a persistent development key ring at an absolute path.</summary>
    public static IServiceCollection AddDevelopmentDataProtectionKeyRing(this IServiceCollection services, string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);

        if (!Path.IsPathFullyQualified(absolutePath))
            throw new ArgumentException("The development key ring path must be absolute.", nameof(absolutePath));


        services.AddSingleton<IDataProtectionKeyRing>(provider =>
        {
            DevelopmentDataProtectionKeyRing.EnsureSupportedPlatform();
            var environment = provider.GetRequiredService<IHostEnvironment>();

            if (!environment.IsDevelopment())
                throw new InvalidOperationException("The development data protection key ring can be used only in the Development environment.");


            return new DevelopmentDataProtectionKeyRing(absolutePath);
        });
        return services;
    }
}
