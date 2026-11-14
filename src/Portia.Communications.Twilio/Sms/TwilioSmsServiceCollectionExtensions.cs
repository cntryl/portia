namespace Cntryl.Portia.Communications.Twilio;

/// <summary>Configures the Twilio SMS provider.</summary>
public sealed class TwilioSmsBuilder
{
    internal HttpClient? Client { get; private set; }

    internal Uri? StatusCallbackAddress { get; private set; }

    /// <summary>Sets the HTTP client used to call Twilio. It must not retry requests.</summary>
    /// <param name="client">The HTTP client.</param>
    /// <returns>This builder.</returns>
    public TwilioSmsBuilder UseHttpClient(HttpClient client)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        return this;
    }

    /// <summary>
    ///     Requests status callbacks at the public HTTPS address where the host maps
    ///     <c>MapTwilioStatusCallbacks</c>, such as <c>https://api.example.com/twilio/status</c>.
    /// </summary>
    /// <param name="publicAddress">The address Twilio reaches the endpoint at.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The address is not an absolute HTTPS address without a query.</exception>
    public TwilioSmsBuilder UseStatusCallbacks(Uri publicAddress)
    {
        ArgumentNullException.ThrowIfNull(publicAddress);
        _ = TwilioStatusCallbacks.Normalize(publicAddress);
        StatusCallbackAddress = publicAddress;
        return this;
    }
}

/// <summary>Registers the Twilio SMS provider.</summary>
public static class TwilioSmsServiceCollectionExtensions
{
    /// <summary>
    ///     Registers <see cref="TwilioSmsSender" /> as the application's <see cref="ISmsSender" />. The
    ///     application registers its <see cref="ITwilioTenantResolver" /> and, to receive status callbacks,
    ///     its <see cref="IDeliveryEventReceiver" />.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="configure">Configures the provider.</param>
    /// <returns>The services.</returns>
    public static IServiceCollection AddTwilioSms(this IServiceCollection services, Action<TwilioSmsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var builder = new TwilioSmsBuilder();
        configure?.Invoke(builder);
        var client = builder.Client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var callbacks = builder.StatusCallbackAddress;
        services.AddSingleton(new TwilioStatusCallbackOptions(TwilioStatusCallbacks.Normalize(callbacks)));
        services.AddSingleton<ISmsSender>(provider =>
            new TwilioSmsSender(client, provider.GetRequiredService<ITwilioTenantResolver>(), callbacks));
        return services;
    }
}
