using Microsoft.OpenApi;

namespace Cntryl.Portia;

static class PortiaOpenApiDescriptionCoverage
{
    internal static void Validate(OpenApiDocument document)
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<IOpenApiSchema>(ReferenceEqualityComparer.Instance);
        foreach (var (path, item) in document.Paths)
        {
            foreach (var (method, operation) in item.Operations ?? [])
            {
                var location = $"{method.Method.ToUpperInvariant()} {path} ({operation.OperationId})";
                if (string.IsNullOrWhiteSpace(operation.Summary) && string.IsNullOrWhiteSpace(operation.Description))
                    _ = missing.Add(location + ": operation");
                foreach (var parameter in (item.Parameters ?? []).Concat(operation.Parameters ?? []))
                {
                    Require(parameter.Description, $"{location}: parameter {parameter.In?.ToString().ToLowerInvariant()} {parameter.Name}");
                    Walk(parameter.Schema, location + $": parameter {parameter.Name} schema");
                    Media(parameter.Content, location + $": parameter {parameter.Name}");
                }
                Media(operation.RequestBody?.Content, location + ": request body");
                foreach (var (status, response) in operation.Responses ?? [])
                {
                    var target = response is OpenApiResponseReference responseReference &&
                                 document.Components?.Responses?.TryGetValue(responseReference.Reference.Id!, out var resolved) == true
                        ? resolved : response;
                    Require(response.Description ?? target.Description, $"{location}: response {status}");
                    Media(target.Content, $"{location}: response {status}");
                    foreach (var (name, header) in target.Headers?.AsEnumerable() ?? [])
                        Walk(header.Schema, $"{location}: response {status} header {name} schema");
                }
            }
        }
        foreach (var (name, schema) in document.Components?.Schemas?.AsEnumerable() ?? [])
            Walk(schema, $"schema {name}");
        if (missing.Count > 0)
            throw new InvalidOperationException("OpenAPI descriptions are required:\n" + string.Join("\n", missing));
        return;

        void Require(string? description, string path)
        {
            if (string.IsNullOrWhiteSpace(description))
                _ = missing.Add(path);
        }

        void Media(IDictionary<string, IOpenApiMediaType>? content, string path)
        {
            foreach (var (mediaType, media) in content?.AsEnumerable() ?? [])
                Walk(media.Schema, $"{path} {mediaType} schema");
        }

        void Walk(IOpenApiSchema? schema, string path)
        {
            if (schema is null || !visited.Add(schema))
                return;
            if (schema is OpenApiSchemaReference reference)
            {
                Walk(reference.Target, $"schema {reference.Reference.Id}");
                return;
            }
            if (schema.Type?.HasFlag(JsonSchemaType.Object) == true || schema.Properties?.Count > 0)
                Require(schema.Description, path);
            foreach (var (name, property) in schema.Properties?.AsEnumerable() ?? [])
            {
                Require(property.Description, $"{path}: property {name}");
                Walk(property, $"{path}: property {name} schema");
            }
            Walk(schema.Items, path + " items");
            Walk(schema.AdditionalProperties, path + " dictionary values");
            Walk(schema.Not, path + " not");
            foreach (var (branches, keyword) in new[]
                     { (schema.AllOf, "allOf"), (schema.AnyOf, "anyOf"), (schema.OneOf, "oneOf") })
            {
                if (branches is null)
                    continue;
                for (var index = 0; index < branches.Count; index++)
                    Walk(branches[index], $"{path} {keyword}[{index}]");
            }
        }
    }
}
