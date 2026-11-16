# Contract metadata and API documentation — 0.14.1

## Scope and dependency boundary

This release includes the full requested set: standard validation attributes, friendly titles,
deprecation, accurate defaults, input/output directionality, typed examples, readable stable schema
names, stable operation IDs/tags/Markdown, HTTP security and response/header metadata, document
information, and executable contract gates.

Request/model projects can depend on standard .NET attributes, System.Text.Json, and transport-neutral
Portia abstractions. They must not reference ASP.NET Core, Microsoft.OpenApi, or the MCP SDK merely
to annotate a request. HTTP schemes, statuses, headers, media types, tags, and server URLs are
configured in the HTTP adapter. MCP translates shared contract facts into its own schemas.

## Shared compiled metadata

Extend the generated JSON/context metadata into transport-neutral type/member descriptors. Carry
Description, Display.Description, Display.Name/DisplayName, Obsolete, known defaults, serializer
access/direction, validation rules, and typed value accessors. Standard attributes remain the normal
model-authoring surface. Explicit application/endpoint configuration overrides descriptive metadata;
it cannot silently override validation behavior.

Generated context factories export descriptors for referenced contracts. Handler registrations also
supply descriptors for local requests that need no JSON context. Runtime traversal uses generated
getters and collection/dictionary-value accessors, never reflection member scanning. Metadata is
application-owned and frozen with composition. Nullable wrappers share their underlying type facts;
member-specific facts stay local and never mutate shared schemas.

## Validation and faithful schemas

Support Required, Range, MinLength, MaxLength, StringLength, RegularExpression, EmailAddress and Url.
Generate built-in attribute validation with typed accessors, including nested models, collection items
and dictionary values. Follow BCL value-validation semantics, including AllowEmptyStrings, nulls,
exclusive bounds, collection lengths, and literal ErrorMessage. Guard recursive graphs and observe
cancellation. Invalid attribute declarations fail composition or compilation as configuration errors.
Reflection-dependent custom converter/resource overloads must be compiled into typed access or
rejected with an actionable diagnostic; never weaken a rule silently.

Run structural validation after authorization and before request behaviors/guards/handlers, for
unary, result-bearing and streaming requests. Authorization-only ingress preflight also validates
after authorization so HTTP asynchronous submission can reject invalid input before enqueueing.
Local dispatch and worker dispatch use the same rules. Validation failures are expected Validation
outcomes, not processing faults. These checks do not replace authoritative aggregate invariants or
cross-aggregate participant outcomes.

Schema translation must distinguish member presence, nullable values, empty strings and defaults.
A Required value rule does not automatically imply wire presence when an effective non-null default
already satisfies it. Document only constraints faithful to actual binding/validation. Regex schemas
must account for whole-value matching, optional empty values and .NET versus ECMA-262 syntax;
unsupported patterns receive explicit diagnostics or a clearly identified nonportable annotation,
not a misleading portable pattern. Formats are informative annotations; runtime validation remains
authoritative. Contradictory known constructor and DefaultValue defaults fail configuration.

Directionality follows actual serializer and binder access, including constructor binding and
JsonIgnore WhenReading/WhenWriting. ReadOnly/Editable UI hints do not silently change API write
permissions. Input and output exports can differ while preserving one shared source of facts.

## Typed examples

Provide application composition for named typed contract examples, with optional input/output use
and summary. Serialize using Portia's frozen JSON resolver/converters. Validate examples against
applicable rules and directionality; invalid examples fail generation/qualification. Shared examples
feed OpenAPI schema examples and MCP JSON Schema examples. HTTP-specific request/response examples
are configured beside endpoints and projected to the actual route/query/body/media contract. They
may override shared examples without changing the DTO. Example payloads are values, not JSON strings
or reflection-based sample generation.

## OpenAPI adapter

- Use readable, deterministic type/context identities for schema components. Generic/namespace and
  representation collisions must be resolved deterministically. Documentation-only changes to
  descriptions, titles, examples, deprecation or defaults never rename a component. Preserve local
  converter/nullability variants and recursive pointer targets.
- Honor endpoint WithName for explicit operation IDs, retain generated defaults, and detect duplicate
  IDs. Use standard endpoint WithTags/WithSummary/WithDescription for organization and Markdown.
  These APIs stay in host composition, off request models.
- Add typed status-specific response/header/example declarations to the existing Produces and
  DescribeResponse surface. Describe actual emitted statuses and content. Never advertise a response
  solely because a description/example mentions it.
- Configure OpenAPI security schemes in HTTP options and associate them with actual ASP.NET
  authentication/authorization policies or explicit endpoint metadata. Preserve AllowAnonymous;
  represent alternatives versus combined credentials correctly. Portia permissions are not invented
  OAuth scopes. Custom application authorization needs explicit documentation mapping.
- Expose application-owned document title/version/description, servers, external documentation,
  contact/license and terms using HTTP document options. HTTP settings do not leak into core/MCP.

## Sequence and qualification

Jev 1.14.0 rated validation highest in consumer value (3.62/4) and semantic risk (3.69/4). Titles,
operation organization and deprecation are smaller wins. Its numeric estimates are advisory; all
features are in scope. Dependency order takes precedence over the aggregate ranking:

1. Shared metadata, stable component identities, titles/deprecation/defaults/directionality.
2. Runtime validation and schema parity across HTTP, MCP, local and worker dispatch.
3. Shared/endpoint examples, operation/document organization, response and security enrichment.
4. Packed/native consumers, generated-document validation and final implementation review.

For every behavior change, add an Arrange/Act/Assert regression, observe the expected failure,
then implement and observe green. Verify renamed members, generic/namespace collisions, recursive
and converted graphs, invalid defaults/examples, unauthorized invalid input, queue preflight and
stream rejection. Compile an annotated request-model project without an ASP.NET dependency and
check its references. Validate JSON and YAML, schema references and actual wire behavior. Preserve
existing authorization, cancellation, fencing, replay, recovery and aggregate authority guarantees.
Use Jev again on the concrete design and implementation, independently verifying its judgments.
