using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Cntryl.Portia;

/// <summary>Registers Portia's dependency-free diagnostic sources with OpenTelemetry hosting.</summary>
public static class PortiaOpenTelemetryBuilderExtensions
{
    /// <summary>Enables Portia traces, metrics, and the standard <c>ILogger</c> bridge.</summary>
    /// <param name="builder">The application's OpenTelemetry hosting builder.</param>
    /// <returns>The supplied builder for application-owned exporter and resource configuration.</returns>
    public static OpenTelemetryBuilder WithPortia(this OpenTelemetryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Services.Any(service => service.ServiceType == typeof(PortiaTelemetryRegistration)))
        {
            return builder;
        }

        _ = builder.Services.AddSingleton<PortiaTelemetryRegistration>();
        return builder
            .WithTracing(tracing => tracing.AddSource(PortiaTelemetry.SourceName))
            .WithMetrics(metrics => metrics.AddMeter(PortiaTelemetry.SourceName))
            .WithLogging();
    }

    sealed class PortiaTelemetryRegistration;
}
