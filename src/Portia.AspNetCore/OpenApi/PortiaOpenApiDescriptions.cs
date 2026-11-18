using Microsoft.OpenApi;

namespace Cntryl.Portia;

sealed record PortiaOpenApiDescriptions(
    IReadOnlyDictionary<string, string> Members,
    IReadOnlyDictionary<int, string> Responses,
    string? SuccessResponse)
{
    internal static IOpenApiSchema Annotate(IOpenApiSchema schema, string? description)
    {
        if (description is null)
            return schema;
        if (schema is OpenApiSchemaReference reference)
        {
            var copy = (OpenApiSchemaReference)reference.CreateShallowCopy();
            copy.Description = description;
            return copy;
        }

        if (schema is OpenApiSchema concrete)
        {
            var copy = (OpenApiSchema)concrete.CreateShallowCopy();
            copy.Description = description;
            return copy;
        }

        return schema;
    }

    internal void ApplyResponses(OpenApiOperation operation)
    {
        var descriptions = Responses.ToDictionary(
            item => item.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item => item.Value, StringComparer.Ordinal);
        if (SuccessResponse is not null)
        {
            var statuses = operation.Responses!.Keys.Where(status =>
                int.TryParse(status, out var code) && code is >= 200 and < 300).ToArray();
            if (statuses.Length != 1)
                throw new InvalidOperationException(
                    $"OpenAPI operation '{operation.OperationId}' needs a status code for DescribeResponse: it has {statuses.Length} success responses.");
            if (!descriptions.TryAdd(statuses[0], SuccessResponse))
                throw new InvalidOperationException($"OpenAPI response '{statuses[0]}' is described more than once.");
        }

        foreach (var (status, description) in descriptions)
        {
            if (!operation.Responses!.TryGetValue(status, out var response))
                throw new InvalidOperationException(
                    $"OpenAPI operation '{operation.OperationId}' does not emit described response '{status}'.");
            // Reference siblings are legal in OpenAPI 3.1. Never edit a shared component's target.
            var copy = response.CreateShallowCopy();
            if (copy is OpenApiResponseReference reference)
                reference.Description = description;
            else if (copy is OpenApiResponse concrete)
                concrete.Description = description;
            operation.Responses[status] = copy;
        }
    }
}
