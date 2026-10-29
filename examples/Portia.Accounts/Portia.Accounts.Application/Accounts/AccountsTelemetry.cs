namespace Portia.Accounts;

/// <summary>Application-owned OpenTelemetry setup shared by both executable hosts.</summary>
public static class AccountsTelemetry
{
    /// <summary>Registers Portia traces, metrics, structured logs, and OTLP export for a host.</summary>
    /// <param name="services">Host services.</param>
    /// <param name="serviceName">The stable OpenTelemetry service name.</param>
    /// <param name="configureTracing">Optional host-specific tracing instrumentation.</param>
    /// <returns>The same services collection.</returns>
    public static IServiceCollection AddAccountsTelemetry(this IServiceCollection services, string serviceName,
        Action<TracerProviderBuilder>? configureTracing = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        _ = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithPortia()
            .WithTracing(tracing =>
            {
                _ = tracing.AddSource(PortiaTelemetry.SourceName);
                configureTracing?.Invoke(tracing);
                _ = tracing.AddOtlpExporter();
            })
            .WithMetrics(metrics => metrics.AddOtlpExporter())
            .WithLogging(logging => logging.AddOtlpExporter());
        return services;
    }
}
