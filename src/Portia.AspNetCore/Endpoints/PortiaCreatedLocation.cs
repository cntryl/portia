using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Cntryl.Portia;

sealed record PortiaCreatedLocation(string Template)
{
    public string Resolve(HttpContext context) => Regex.Replace(Template, "\\{([^{}]+)\\}", match =>
    {
        var name = match.Groups[1].Value;
        if (!context.Request.RouteValues.TryGetValue(name, out var value) || value is null)
            throw new InvalidOperationException($"Matched route value '{name}' is missing for the Created URI.");
        return Uri.EscapeDataString(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!);
    });
}
