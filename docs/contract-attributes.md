# Contract attributes

Portia compiles standard .NET model annotations into metadata shared by request execution, OpenAPI,
and MCP. Model projects need standard .NET and Portia abstractions; they do not need ASP.NET Core,
Microsoft.OpenApi, or the MCP SDK to describe their requests.

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Cntryl.Portia;

[Description("Creates a catalog item.")]
[DisplayName("Create catalog item")]
public sealed record CreateItem(
    [Required, StringLength(120), Display(Name = "Item name")]
    string? Name,
    [Range(1, 100)] int Quantity = 1) : IRequest<Item>, ICallable;
```

Matching constructor-parameter annotations describe and validate the corresponding member. A positional
record can use this syntax without repeating the same facts in property attributes and endpoint
configuration. Ordinary property and field annotations are supported too.

## Value validation and presence

`[Required]` validates an existing value. It rejects null and, by default, empty or whitespace-only
strings. `AllowEmptyStrings = true` permits empty and whitespace strings. It accepts non-null value
types, including integer zero and boolean false.

`[JsonRequired]` requires a JSON property to appear. An explicit null satisfies presence; nullability
and value rules determine whether that value is legal. Use `property:` when placing this JSON attribute
on a positional record property:

```csharp
using System.Text.Json.Serialization;

public sealed record UpdateItem(
    [property: JsonRequired] string? Note,
    [Required] string? Name = "Untitled") : IRequest;
```

Here JSON must contain `note`, which may be null. Omitting `name` supplies a valid constructor default;
providing null or whitespace for it fails value validation. JSON presence is separate from query/form
binding. See [System.Text.Json required properties](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/required-properties).

Authorization runs first. Standard value validation then runs before request behaviors, guards, and
handlers, including local dispatch, queued worker dispatch, streams, and acceptance preflight.
A failed rule produces a non-transient `Validation` outcome. A stream fails with
`RequestGuardException` before producing an item. These structural checks do not replace aggregate
invariants or authoritative participant outcomes; see [validation authority](validation-authority.md).

Purely local requests do not require JSON roots. Requests that cross a JSON boundary still require
explicit serializer roots on a `[PortiaJsonContext]`.

## Supported facts

| Annotation | Meaning |
| --- | --- |
| `Description`, literal `Display.Description` | Shared type/member description |
| Literal `Display.Name`, `DisplayName` | Friendly title |
| `Obsolete` | Deprecation marker and explanation |
| `Required` | Non-null/non-empty value validation |
| `Range` with numeric bounds | BCL range validation, including exclusive bounds |
| `MinLength`, `MaxLength`, `StringLength` | String or supported collection length validation |
| `RegularExpression` | Whole-value .NET regex validation; null/empty remain valid without another rule |
| `EmailAddress`, `Url` | BCL value validation and informative schema formats |
| `DefaultValue` with a literal value | Documentation default; it does not initialize the member |
| Constructor defaults and known literal initializers | Effective defaults used when documenting omission |
| `JsonIgnore` conditions and live serializer access | Separate JSON input/output projections |

A known `DefaultValue` that contradicts the constructor default is rejected. A default rejected by
value validation is not advertised as an accepted schema default. Invalid declarations and rules
without accessible generated value access fail configuration or compilation; rules are never
silently skipped.

Validation traverses generated model paths, collection elements, and dictionary values, including
interface-typed collections. Recursive references are visited once. Traversal observes cancellation
and limits depth, graph size, and collected errors. It does not enumerate or inspect members through
reflection.

`ReadOnly` and `Editable` are UI hints. They do not grant or remove permission to write a value.
JSON direction follows the serializer's actual access and ignore/population configuration.

## Portable schemas

Some .NET rules have no exactly equivalent portable JSON Schema keyword:

- .NET string length counts UTF-16 code units; JSON Schema counts Unicode characters. Portia emits
  conservative Unicode bounds and retains exact unit bounds in `x-portia-utf16-minLength` and
  `x-portia-utf16-maxLength`. This avoids rejecting a valid astral character solely because the two
  systems count it differently.
- Integer `Range` bounds use BCL integer conversion. Prefer `[Range(1D, 10D)]` for fractional values.
  Conversion details are retained in `x-portia-range`; numeric bounds are emitted when they describe
  the accepted values faithfully.
- Portable regex patterns preserve whole-value matching and the BCL's optional empty value. Other
  .NET patterns remain in `x-portia-dotnet-pattern`, without a misleading ECMA-262 pattern.
- `x-portia-reject-whitespace` retains the non-whitespace part of the default `Required` string rule.
  Formats remain informative; runtime validation enforces the BCL rule.

See [JSON Schema string validation](https://json-schema.org/draft/2020-12/json-schema-validation#section-6.3)
and [.NET Range implementation](https://source.dot.net/System.ComponentModel.Annotations/System/ComponentModel/DataAnnotations/RangeAttribute.cs.html).

Type-converter overloads such as `Range(Type, string, string)` and `DefaultValue(Type, string)`, and
resource-backed validation messages, require explicit typed application validation instead of
reflection-dependent conversion. `PORTIA031` reports these declarations on Portia contracts.
Literal `ErrorMessage` is supported. Resource-backed display text remains application-owned.
