# Contract descriptions

See [contract attributes](contract-attributes.md) for value validation, friendly names, defaults, typed
examples, and HTTP document/security enrichment.

Put reusable descriptions beside the model using standard .NET attributes:

```csharp
using System.ComponentModel;

[Description("A thing.")]
public sealed record ThingView(
    [property: Description("Thing identifier.")] Uuid Id,
    [property: Description("Display name.")] string Name);
```

Portia reads literal `[Description]` text at compile time and carries it through the configured
`[PortiaJsonContext]` contracts, including nested models and referenced context assemblies. The
same attributes describe OpenAPI schemas and MCP input/output schemas. On a request type,
`[Description]` also supplies the MCP tool description and the default OpenAPI operation description.

For properties, literal `[Display(Description = "...")]` from
`System.ComponentModel.DataAnnotations` is supported as a fallback when `[Description]` is absent.
Resource-backed `Display` descriptions require application-owned localization and explicit text
configuration. Attribute text is emitted as generated metadata; no runtime assembly/member scans
or expression compilation are needed. Existing JSON naming policies, `[JsonPropertyName]`, resolver
renames, converters, nullability, defaults, and requiredness retain their usual meaning.

## Shared defaults and overrides

No fluent registration is needed for an attributed model. Shared wire-name defaults are useful
for recurring fields that do not carry their own description:

```csharp
builder.Services.AddPortia().AddHttp(options =>
{
    options.OpenApi.DescribeMember("limit", "Maximum results to return.");
    options.OpenApi.DescribeMember("cursor", "Continue after this cursor.");
});

app.MapPortiaGet<ListThings, ThingView[]>("/v1/things")
    .WithSummary("List things.");
```

`WithSummary` / `WithDescription` supply endpoint-specific operation text. A request type's
`[Description]` supplies the operation description when `WithDescription` is absent.

For external types or application-specific overrides, use
`options.OpenApi.DescribeSchema<ThingView>("Application thing.", d => d.Property(x => x.Name, "Application display name."))`.
For one endpoint, `Describe(x => x.Limit, text)` overrides its parameter or body property text.
Both selectors take direct CLR members; renaming the member breaks compilation.

OpenAPI member precedence is:

1. Endpoint `Describe`.
2. Application `DescribeSchema<T>().Property` registration.
3. Model `[Description]`, then literal `[Display(Description = ...)]`.
4. Shared `DescribeMember` for the exact wire name.
5. Existing serializer annotations or the value type's schema description.

Schema descriptions use the explicit fluent registration before the type attribute. Shared names
are ordinal and case-sensitive. Route/query/form names follow binding; JSON body names follow the
JSON contract. `Describe` covers constructor-bound members only. Custom `OnBind` inputs use:

```csharp
c.Parameter<string>("X-Region", PortiaHttpParameterLocation.Header,
    description: "Region selected by the caller.");
```

`DescribeResponse(text)` optionally replaces the standard response text, such as `OK` or
`No Content`, for the single emitted 2xx response. With multiple 2xx responses (including an
asynchronous 202 receipt), specify a status: `DescribeResponse(200, text)`.
`DescribeResponse(409, text)` explains an existing error response locally. A description does not
declare a new status or change runtime behavior.

Fluent schema registrations apply to exact closed CLR types. Nullable value types fall back to
their underlying type. For a converter mapping, the original contract's schema description overrides
its representation's description; representation properties use their own metadata. Duplicate
fluent registrations and blank fluent text are errors. Configure before generating the document:
JSON and YAML are rendered together and cached.

MCP's `DescribedAs` and `DescribeProperty` override model attributes for that tool. Existing MCP
XML documentation remains a fallback for contracts without description attributes. OpenAPI-specific
options apply to OpenAPI; attributes are the shared contract metadata for both transports.

## Enforcing OpenAPI coverage

Set `options.OpenApi.RequireDescriptions = true` and generate the document in a build check,
integration test, or external consumer. Compiling the application alone does not generate OpenAPI.
Generation throws one `InvalidOperationException` listing sorted, deduplicated missing locations.

An operation needs a summary or description. Parameters and responses need descriptions. Object
schemas and their properties need descriptions, including synthetic request-body objects,
dictionaries, nested objects and polymorphic branches. Primitive leaves, array containers and
reference wrappers need no separate text. A property's referenced schema description can satisfy
the property's description. Portia supplies text for its own problem payload, queue receipt and
protocol fields. Attributes can satisfy coverage without any fluent description registration.

Coverage includes ordinary ASP.NET endpoints and components in the same document. It runs at
Portia's document-transformer stage; application transformers registered after it own any further
edits. Failed generation is not cached as success. Coverage checks presence, not editorial quality.
