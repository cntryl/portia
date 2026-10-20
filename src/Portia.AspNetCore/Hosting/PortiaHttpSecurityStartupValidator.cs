using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace Cntryl.Portia;

sealed class PortiaSecureHostMarker
{
    readonly object _gate = new();
    readonly HashSet<EndpointDataSource> _dataSources = new(ReferenceEqualityComparer.Instance);

    public void Register(ICollection<EndpointDataSource> dataSources)
    {
        lock (_gate)
            foreach (var dataSource in dataSources)
                _dataSources.Add(dataSource);
    }

    public Endpoint[] GetEndpoints()
    {
        lock (_gate)
            return _dataSources.SelectMany(source => source.Endpoints).ToArray();
    }
}

sealed record PortiaHttpEndpointMarker(Type? RequestType, string Method);

sealed class PortiaHttpSecurityStartupValidator(
    PortiaSecureHostMarker secureHost,
    IAuthenticationSchemeProvider authenticationSchemes) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var endpoints = secureHost.GetEndpoints()
            .Where(endpoint => endpoint.Metadata.GetMetadata<PortiaOpenApiOperation>() is not null
                               || endpoint.Metadata.GetMetadata<PortiaHttpEndpointMarker>() is not null)
            .ToArray();
        if (await authenticationSchemes.GetDefaultAuthenticateSchemeAsync().ConfigureAwait(false) is null
            || await authenticationSchemes.GetDefaultChallengeSchemeAsync().ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException(
                "RequireSecureHost() needs a default ASP.NET authentication and challenge scheme. Configure one with AddAuthentication(defaultScheme) before starting the host.");
        }
        if (endpoints.Length == 0)
            return;

        var unprotected = endpoints.Where(endpoint =>
                endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null
                && !endpoint.Metadata.OfType<IAuthorizeData>().Any())
            .Select(endpoint => endpoint.DisplayName ?? "<unnamed Portia HTTP endpoint>")
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unprotected.Length > 0)
        {
            throw new InvalidOperationException(
                $"RequireSecureHost() found Portia HTTP endpoints without ASP.NET authorization metadata: {string.Join(", ", unprotected)}. Apply RequireAuthorization(), or explicitly mark the route AllowAnonymous().");
        }

        var unsafeGet = endpoints.Where(endpoint =>
                endpoint.Metadata.GetMetadata<PortiaHttpEndpointMarker>() is { Method: "GET", RequestType: { } requestType }
                && !typeof(IReadOnlyRequest).IsAssignableFrom(requestType))
            .Select(endpoint => endpoint.DisplayName ?? "<unnamed Portia GET endpoint>")
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unsafeGet.Length > 0)
        {
            throw new InvalidOperationException(
                $"RequireSecureHost() requires GET endpoints to map requests that implement IReadOnlyRequest: {string.Join(", ", unsafeGet)}. GET must not change application state or cause externally visible side effects.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.CompletedTask;
    }
}
