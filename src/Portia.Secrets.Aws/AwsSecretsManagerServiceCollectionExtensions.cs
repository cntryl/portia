using System;
using Amazon.SecretsManager;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia.Secrets.Aws;

/// <summary>Registers the AWS Secrets Manager provider in a .NET host.</summary>
public static class AwsSecretsManagerServiceCollectionExtensions
{
    /// <summary>Registers a secret reader using the supplied Secrets Manager client.</summary>
    public static IServiceCollection AddAwsSecretsManager(this IServiceCollection services, IAmazonSecretsManager client)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSingleton<ISecretReader>(new AwsSecretReader(client));
    }
}
