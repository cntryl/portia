namespace Cntryl.Portia.Communications.Azure;

/// <summary>Configures the Azure Communication Services mail provider.</summary>
public sealed class AzureMailBuilder
{
    internal EmailClient? Client { get; private set; }

    /// <summary>Connects with a Communication Services connection string.</summary>
    /// <param name="connectionString">The resource's connection string.</param>
    /// <returns>This builder.</returns>
    public AzureMailBuilder UseConnectionString(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        Client = new EmailClient(connectionString, WithoutRetries());
        return this;
    }

    /// <summary>Connects to a Communication Services endpoint with a token credential, such as a managed identity.</summary>
    /// <param name="endpoint">The resource endpoint.</param>
    /// <param name="credential">The credential.</param>
    /// <returns>This builder.</returns>
    public AzureMailBuilder UseEndpoint(Uri endpoint, TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credential);
        Client = new EmailClient(endpoint, credential, WithoutRetries());
        return this;
    }

    // The send is not idempotent, so the SDK must never repeat it.
    static EmailClientOptions WithoutRetries()
    {
        var options = new EmailClientOptions();
        options.Retry.MaxRetries = 0;
        return options;
    }
}

/// <summary>Registers the Azure Communication Services mail provider.</summary>
public static class AzureMailServiceCollectionExtensions
{
    /// <summary>
    ///     Registers <see cref="AzureMailSender" /> as the application's <see cref="IMailSender" />. The
    ///     application registers its <see cref="IAzureMailTenantResolver" />.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="configure">Configures the provider; it must choose a connection.</param>
    /// <returns>The services.</returns>
    /// <exception cref="InvalidOperationException">No connection was configured.</exception>
    public static IServiceCollection AddAzureMail(this IServiceCollection services, Action<AzureMailBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new AzureMailBuilder();
        configure(builder);
        var client = builder.Client ?? throw new InvalidOperationException(
            "An Azure Communication Services connection must be configured.");
        services.AddSingleton<IMailSender>(provider =>
            new AzureMailSender(client, provider.GetRequiredService<IAzureMailTenantResolver>()));
        return services;
    }
}
