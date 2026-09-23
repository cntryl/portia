using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Cntryl.Portia;

public static partial class PortiaHttpBinding
{
    /// <summary>
    ///     Refuses a state-changing browser request from another origin unless the application's CORS
    ///     pipeline allows that origin.
    /// </summary>
    /// <param name="context">The current HTTP request.</param>
    /// <returns>A 403 problem result, or <see langword="null" /> when the request may proceed.</returns>
    public static IResult? RejectCrossOrigin(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return PortiaCrossOrigin.IsAllowed(context)
            ? null
            : Problem(StatusCodes.Status403Forbidden, "Cross-origin request rejected.");
    }

    /// <summary>Reports whether default binding accepts HTML form bodies for an endpoint.</summary>
    /// <param name="endpointMetadata">The endpoint's metadata.</param>
    /// <param name="services">The application services.</param>
    /// <returns>
    ///     <see langword="true" /> when the application registers antiforgery or the endpoint opts out of it.
    /// </returns>
    internal static bool AcceptsFormBody(IEnumerable<object> endpointMetadata, IServiceProvider services) =>
        endpointMetadata.OfType<IAntiforgeryMetadata>().LastOrDefault() is { RequiresValidation: false } ||
        services.GetService<IAntiforgery>() is not null;

    // A form post can be forged cross-site where a JSON post cannot, so default form binding fails
    // closed: forms are refused unless antiforgery is registered to validate them or the endpoint
    // explicitly opts out with DisableAntiforgery().
    static async ValueTask ValidateAntiforgeryAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<IAntiforgeryMetadata>() is { RequiresValidation: false })
            return;
        if (context.Features.Get<IAntiforgeryValidationFeature>() is { } feature)
        {
            if (!feature.IsValid)
                throw new AntiforgeryValidationException("Invalid antiforgery token.", feature.Error);
            return;
        }

        if (context.RequestServices.GetService<IAntiforgery>() is not { } antiforgery)
            throw new HttpUnsupportedMediaTypeException("Form bodies require antiforgery validation.");
        await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
    }
}
