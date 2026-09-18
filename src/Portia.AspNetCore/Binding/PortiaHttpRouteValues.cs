using Microsoft.AspNetCore.Http;

namespace Cntryl.Portia;

sealed record PortiaHttpRouteValues(Func<HttpContext, RequestRouteValues> Resolve);
