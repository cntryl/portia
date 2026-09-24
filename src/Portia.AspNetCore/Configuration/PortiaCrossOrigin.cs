using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

// Browsers attach cookies and other ambient credentials to requests another site starts, and a form,
// text/plain, or bodiless request reaches the server without a CORS preflight. A state-changing request
// is therefore accepted only when the browser reports it as same-origin or the application's CORS
// pipeline allows its origin. Clients that are not browsers send neither Sec-Fetch-Site nor Origin, so
// they are unaffected. This is not DNS-rebinding protection: a page on a host name rebound to this server
// is same-origin with the host it reaches, and only ASP.NET Core host filtering (AllowedHosts) refuses it.
static class PortiaCrossOrigin
{
    internal static bool IsAllowed(HttpContext context)
    {
        var request = context.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) ||
            HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method))
            return true;

        var fetchSite = request.Headers["Sec-Fetch-Site"];
        if (fetchSite.Count > 0)
        {
            // The browser sets fetch metadata itself, so it decides whenever it is present.
            if (fetchSite.Count == 1 && fetchSite[0] is "same-origin" or "none")
                return true;
        }
        else
        {
            var origin = request.Headers.Origin;
            if (origin.Count == 0)
                return true;
            if (origin.Count == 1 && MatchesHost(origin[0], request.Host))
                return true;
        }

        return context.Features.Get<PortiaCorsDecision>() is { OriginAllowed: true };
    }

    // Registers the CORS decision recorder once, wrapping whichever CORS service the application has.
    // CORS services registered later keep the recorder, because AddCors only adds its service when none
    // is present.
    internal static void AddCorsDecisions(IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(PortiaCorsDecisionMarker)))
            return;

        _ = services.AddSingleton<PortiaCorsDecisionMarker>();
        _ = services.AddCors();
        var inner = services.Last(descriptor =>
            descriptor.ServiceType == typeof(ICorsService) && !descriptor.IsKeyedService);
        _ = services.Remove(inner);
        services.Add(ServiceDescriptor.Describe(typeof(ICorsService),
            provider => new PortiaCorsService(CreateInner(provider, inner)), inner.Lifetime));
    }

    static ICorsService CreateInner(IServiceProvider provider, ServiceDescriptor descriptor) =>
        (ICorsService)(descriptor.ImplementationInstance
                       ?? descriptor.ImplementationFactory?.Invoke(provider)
                       ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!));

    // The Host header carries no scheme, so this compares the host and effective port; browsers that
    // send fetch metadata never reach this fallback.
    static bool MatchesHost(string? origin, HostString host) =>
        host.HasValue &&
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
        string.Equals(uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost, host.Host,
            StringComparison.OrdinalIgnoreCase) &&
        (host.Port is { } port ? uri.Port == port : uri.IsDefaultPort);

    // The CORS middleware chooses the policy for each request (a named middleware policy, an endpoint
    // policy, or the default policy) and exposes nothing about its decision. Recording the result the
    // CORS service computed lets Portia trust exactly the origins the pipeline allowed.
    sealed class PortiaCorsService(ICorsService inner) : ICorsService
    {
        public CorsResult EvaluatePolicy(HttpContext context, CorsPolicy policy)
        {
            var result = inner.EvaluatePolicy(context, policy);
            context.Features.Set(new PortiaCorsDecision(result.IsOriginAllowed));
            return result;
        }

        public void ApplyResult(CorsResult result, HttpResponse response) => inner.ApplyResult(result, response);
    }

    sealed record PortiaCorsDecision(bool OriginAllowed);

    sealed class PortiaCorsDecisionMarker;
}
