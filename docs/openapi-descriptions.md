# OpenAPI descriptions

Describe each thing once. Shared wire-name text covers recurring parameters; type registrations
cover reusable schemas and their properties:

```csharp
builder.Services.AddPortia().AddHttp(options =>
{
    options.OpenApi.DescribeMember("limit", "Maximum results to return.");
    options.OpenApi.DescribeMember("cursor", "Continue after this cursor.");
    options.OpenApi.DescribeSchema<ThingView>("A thing.", d => d
        .Property(x => x.Name, "Display name."));
});

app.MapPortiaGet<ListThings, ThingView[]>("/v1/things")
    .WithSummary("List things.");
```

Endpoint `Describe` is an optional override for a member whose meaning differs at one endpoint;
`DescribeResponse` optionally replaces the standard response text such as `OK` or `No Content`.
Neither is needed in the example above. For a parameter unique to one endpoint, describe it in
that endpoint's configuration instead of registering a shared default.

`Describe` and `Property` take a direct CLR member selector. Renaming the member breaks compilation;
JSON naming policies, `[JsonPropertyName]`, and resolver renames still determine serialized names.
Expressions are inspected, never compiled. No assembly scanning or XML comments are needed.

Member text is selected in this order: endpoint `Describe`, containing type's `Property`, shared
`DescribeMember` for the exact wire name, then serializer annotations. A value type's schema
description supplies a fallback if there is no member text. Shared names are ordinal and
case-sensitive. Route/query/form names follow binding; JSON body names follow the JSON contract.
`Describe` covers constructor-bound members only. For custom `OnBind` inputs, use:

```csharp
c.Parameter<string>("X-Region", PortiaHttpParameterLocation.Header,
    description: "Region selected by the caller.");
```

`DescribeResponse(text)` targets the single emitted 2xx response. When there are multiple
(including an asynchronous 202 receipt), specify the status: `DescribeResponse(200, text)`.
`DescribeResponse(409, text)` can also explain a default error response locally. The status must
exist in the emitted contract. Custom `Produces` declarations determine the available statuses;
describing a response does not declare a new response or change runtime behavior.

Schema registrations apply to exact closed CLR types wherever they occur, including converter
representations, arrays, dictionary values, and polymorphic branches. Nullable value types fall
back to their underlying type registration. For a converter mapping, the original contract's
schema description overrides its representation's schema description; representation properties
use the representation's property registrations. Duplicate registrations and blank text are errors.
Configure before generating the document: JSON and YAML are rendered together and cached.

## Enforcing coverage

Set `options.OpenApi.RequireDescriptions = true` and generate the document in a build check,
integration test, or external consumer. Compiling the application alone does not generate OpenAPI.
Generation throws one `InvalidOperationException` listing sorted, deduplicated missing locations.

An operation needs a summary or description (`WithSummary` / `WithDescription`). Parameters and
responses need descriptions. Object schemas and their properties need descriptions, including the
synthetic request-body object (`DescribeSchema<TRequest>`), dictionary schemas, nested objects and
polymorphic branches. Primitive leaves, array containers and reference wrappers need no separate
text. A property's referenced schema description can satisfy the property's description.
Portia supplies text for its own problem payload, queue receipt and protocol fields.

Coverage includes ordinary ASP.NET endpoints and components in the same document. It runs at
Portia's document-transformer stage; application transformers registered after it own any further
edits. Failed generation is not cached as success. Coverage checks presence, not editorial quality.
