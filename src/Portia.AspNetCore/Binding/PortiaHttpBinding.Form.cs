using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace Cntryl.Portia;

public static partial class PortiaHttpBinding
{
    /// <summary>Reports whether the request carries an HTML form body.</summary>
    /// <param name="context">The current HTTP request.</param>
    /// <returns><see langword="true" /> for <c>application/x-www-form-urlencoded</c> or <c>multipart/form-data</c>.</returns>
    public static bool HasFormBody(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Request.HasFormContentType;
    }

    /// <summary>
    ///     Reads a bounded HTML form body after antiforgery validation. Without a registered antiforgery
    ///     service, forms are refused unless the endpoint disables antiforgery.
    /// </summary>
    /// <param name="context">The current HTTP request.</param>
    /// <param name="ct">A token that can cancel the read.</param>
    /// <returns>The parsed form.</returns>
    /// <exception cref="HttpPayloadTooLargeException">The body exceeds <c>PortiaHttpOptions.MaxJsonBodyBytes</c>.</exception>
    /// <exception cref="AntiforgeryValidationException">The antiforgery token is missing or invalid.</exception>
    /// <exception cref="HttpUnsupportedMediaTypeException">
    ///     Antiforgery is not registered and the endpoint does not disable it.
    /// </exception>
    public static async ValueTask<IFormCollection> ReadFormBodyAsync(HttpContext context, CancellationToken ct)
    {
        EnsureBodyWithinLimit(context);
        try
        {
            await ValidateAntiforgeryAsync(context).ConfigureAwait(false);
            return await context.Request.ReadFormAsync(ct).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException ex) when (HasBodyLimitCause(ex))
        {
            throw new HttpPayloadTooLargeException();
        }
        catch (BadHttpRequestException ex) when (IsServerBodyLimit(ex))
        {
            throw new HttpPayloadTooLargeException();
        }
        catch (InvalidDataException ex)
        {
            throw new BadHttpRequestException("Malformed form body.", ex);
        }
    }

    /// <summary>Reads one form field value.</summary>
    /// <param name="form">The parsed form.</param>
    /// <param name="name">The field name.</param>
    /// <param name="emptyIsMissing">Whether an empty value is treated as an absent field.</param>
    /// <returns>The value, or <see langword="null" /> when the field is absent.</returns>
    /// <exception cref="BadHttpRequestException">The field was submitted more than once.</exception>
    public static string? ReadForm(IFormCollection form, string name, bool emptyIsMissing)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!form.TryGetValue(name, out var values) || values.Count == 0)
            return null;
        if (values.Count != 1)
            throw new BadHttpRequestException($"Expected one value for '{name}'.");
        return emptyIsMissing && string.IsNullOrEmpty(values[0]) ? null : values[0];
    }

    /// <summary>
    ///     Reads a form Boolean field. ASP.NET's checkbox helpers post the checkbox value followed by a
    ///     hidden <c>false</c> field of the same name, so repeated values bind <c>true</c> when any is set.
    /// </summary>
    /// <param name="form">The parsed form.</param>
    /// <param name="name">The field name.</param>
    /// <returns><c>true</c>, <c>false</c>, the first unparseable value, or <see langword="null" /> when absent or blank.</returns>
    public static string? ReadFormBoolean(IFormCollection form, string name)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!form.TryGetValue(name, out var values))
            return null;
        var isSet = false;
        var found = false;
        foreach (var value in values)
        {
            if (string.IsNullOrEmpty(value))
                continue;
            if (!TryParseFormBoolean(value, out var parsed))
                return value;
            found = true;
            isSet |= parsed;
        }

        return !found ? null : isSet ? bool.TrueString : bool.FalseString;
    }

    /// <summary>Parses a form Boolean, accepting the <c>on</c> value a checked HTML checkbox submits.</summary>
    /// <param name="value">The submitted field value.</param>
    /// <param name="result">The parsed value.</param>
    /// <returns><see langword="true" /> when the value is <c>on</c>, <c>true</c>, or <c>false</c>.</returns>
    public static bool TryParseFormBoolean(string? value, out bool result)
    {
        if (string.Equals(value, "on", StringComparison.OrdinalIgnoreCase))
        {
            result = true;
            return true;
        }

        return bool.TryParse(value, out result);
    }
}
