namespace Cntryl.Portia.Communications.Aws;

/// <summary>Configures the Amazon SES mail provider.</summary>
public sealed class AwsMailBuilder
{
    internal IAmazonSimpleEmailServiceV2? Client { get; private set; }

    /// <summary>Sets the SES client used by the sender. Configure it with <c>MaxErrorRetry = 0</c>.</summary>
    /// <param name="client">The SES client.</param>
    /// <returns>This builder.</returns>
    public AwsMailBuilder UseClient(IAmazonSimpleEmailServiceV2 client)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        return this;
    }
}

/// <summary>Registers the Amazon SES mail provider.</summary>
public static class AwsMailServiceCollectionExtensions
{
    /// <summary>
    ///     Registers <see cref="AwsMailSender" /> as the application's <see cref="IMailSender" />, and an
    ///     <see cref="AwsMailEventIngester" />. The application registers its
    ///     <see cref="IAwsMailTenantResolver" /> and, to ingest events, its <see cref="IDeliveryEventReceiver" />.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="configure">Configures the provider; it must set the SES client.</param>
    /// <returns>The services.</returns>
    /// <exception cref="InvalidOperationException">No SES client was configured.</exception>
    public static IServiceCollection AddAwsMail(this IServiceCollection services, Action<AwsMailBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new AwsMailBuilder();
        configure(builder);
        var client = builder.Client
            ?? throw new InvalidOperationException("An IAmazonSimpleEmailServiceV2 client must be configured.");
        services.AddSingleton<IMailSender>(provider =>
            new AwsMailSender(client, provider.GetRequiredService<IAwsMailTenantResolver>()));
        services.AddSingleton(provider => new AwsMailEventIngester(provider.GetRequiredService<IDeliveryEventReceiver>()));
        return services;
    }
}
