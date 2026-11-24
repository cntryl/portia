using System;
using Cntryl.Portia.Secrets.Aws;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.DataProtection.Aws;

/// <summary>Registers Portia data protection using AWS Secrets Manager and KMS.</summary>
public static class AwsDataProtectionServiceCollectionExtensions
{
    /// <summary>Registers the protector and hasher with one explicitly configured AWS key ring.</summary>
    public static IServiceCollection AddAwsDataProtection(
        this IServiceCollection services,
        Action<AwsDataProtectionBuilder> configure
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new AwsDataProtectionBuilder();
        configure(builder);
        var reader = new AwsSecretReader(builder.SecretsManager
                                         ?? throw new InvalidOperationException("An IAmazonSecretsManager client must be configured."));
        var kms = builder.Kms
                  ?? throw new InvalidOperationException("An IAmazonKeyManagementService client must be configured.");
        var (secretArn, wrappingKeyArn, ringId) = builder.KeyRing
                                                ?? throw new InvalidOperationException("An AWS data protection key ring must be configured.");
        var lifetime = builder.CacheLifetime;
        services.AddSingleton<IDataProtectionKeyRing>(_ => new AwsDataProtectionKeyRing(
            reader, kms, secretArn, wrappingKeyArn, ringId, lifetime, TimeProvider.System));
        return services.AddPortiaDataProtection();
    }
}
