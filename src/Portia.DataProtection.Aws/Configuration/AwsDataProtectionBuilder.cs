using System;
using Amazon.KeyManagementService;
using Amazon.SecretsManager;

namespace Cntryl.Portia.DataProtection.Aws;

/// <summary>Configures the AWS Secrets Manager and KMS data protection provider.</summary>
public sealed class AwsDataProtectionBuilder
{
    internal IAmazonSecretsManager? SecretsManager { get; private set; }
    internal IAmazonKeyManagementService? Kms { get; private set; }
    internal (string SecretArn, string WrappingKeyArn, string RingId)? KeyRing { get; private set; }
    internal TimeSpan CacheLifetime { get; private set; } = TimeSpan.FromMinutes(5);

    /// <summary>Uses the supplied clients for manifest reads and key unwrapping.</summary>
    public AwsDataProtectionBuilder UseClients(
        IAmazonSecretsManager secretsManager,
        IAmazonKeyManagementService kms
    )
    {
        ArgumentNullException.ThrowIfNull(secretsManager);
        ArgumentNullException.ThrowIfNull(kms);
        SecretsManager = secretsManager;
        Kms = kms;
        return this;
    }

    /// <summary>Selects the manifest secret, KMS wrapping key, and stable Portia ring identity.</summary>
    public AwsDataProtectionBuilder UseKeyRing(string secretArn, string wrappingKeyArn, string ringId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretArn);
        ArgumentException.ThrowIfNullOrWhiteSpace(wrappingKeyArn);

        if (!DataProtectionKeyIds.IsValid(ringId))
            throw new ArgumentException("Ring ID must contain 1–64 ASCII letters, digits, underscores, or hyphens.", nameof(ringId));

        KeyRing = (secretArn, wrappingKeyArn, ringId);
        return this;
    }

    /// <summary>Sets the maximum snapshot age. The default is five minutes and the limit is one hour.</summary>
    public AwsDataProtectionBuilder UseCacheLifetime(TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Cache lifetime must be positive and no more than one hour.");

        CacheLifetime = lifetime;
        return this;
    }
}
