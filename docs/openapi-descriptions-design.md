# OpenAPI descriptions (0.14.0, issue #245)

## Contract

Descriptions are explicit C# configuration, inspected without compiling expressions or scanning
assemblies. They change documentation only. Portia's JSON metadata remains authoritative for
wire names, converter representations, nullability, defaults, and requiredness.

- `options.OpenApi.DescribeSchema<T>(text, d => d.Property(x => x.Member, text))` registers one
  schema description and direct CLR member descriptions. Registrations are exact closed types;
  nullable value types fall back to their underlying type. Repeat registrations are errors.
- `options.OpenApi.DescribeMember("wire_name", text)` registers an ordinal, case-sensitive fallback
  for a serialized property or HTTP parameter name. Repeat registrations are errors.
- Endpoint `Describe(x => x.Member, text)` describes a constructor-bound route/query parameter or
  a top-level body property. It rejects nested/computed selectors and custom binders (whose inputs
  are declared separately). `Parameter<T>(name, location, required, description)` covers custom inputs.
- Endpoint `DescribeResponse(text)` describes the single success response, inferred from the final
  contract. Multiple success statuses require `DescribeResponse(statusCode, text)`. The status
  overload covers any emitted response; referring to a missing response is an error.
- Precedence at member occurrences is endpoint, registered containing-type property, shared wire
  member, then existing serializer annotations. A member description is distinct from the schema
  description of its value type. Endpoint annotations are applied to local schemas or reference
  siblings, never by mutating a referenced component.
- Selectors retain CLR identity. JSON names come from existing serializer metadata (including
  constructor associations and application resolver renames); query/form names keep their binding
  names. Missing registered schema properties fail export instead of silently losing documentation.
- A description registered for an original converter-backed contract overrides its representation
  type description. Member text still has higher precedence. Representation property text applies
  to representation properties.
- Configuration must finish before document generation; cached JSON and YAML use the same document.

## Coverage

`options.OpenApi.RequireDescriptions` is opt-in. A document transformer checks the final document
at Portia's transformer stage, after operation transformation. Later application document
transformers own their edits. Generating the document in CI exercises this check; merely building
an application does not generate a document automatically.

An operation needs a nonblank summary or description. Every parameter and response needs a
nonblank description. Object schemas and their properties need descriptions, including inline
request-body objects, nested objects, dictionary values and polymorphic branches; array wrappers,
primitive leaves and `$ref` wrappers need no independent description. A property can use its
referenced schema's description as a fallback. Portia supplies descriptions for its own problem
response, queue receipt and protocol fields. Non-Portia operations in the same document are checked
as well. Traversal is cycle-safe, visits all schema-bearing request/response media types, and
reports a sorted, deduplicated list of document paths in one exception. A failed generation is
not cached as a successful document.

## Validation

Tests cover each registration scope, precedence, route/query/body/form/custom inputs, response
statuses, shared response isolation, serialized names, recursive and converted schemas, JSON/YAML,
strict coverage success and complete diagnostics, invalid configuration, and independent endpoints.
Existing OpenAPI contract and packed-consumer/AOT checks remain applicable. Jev reviews narrow
contract questions; its answers are advisory and are checked against source and executable tests.

Release preparation bumps all packages to 0.14.0 and adds changelog, usage and migration notes.
Publishing packages and tagging the release remain separate actions.

## Jev review

Reviewed with `jev-1.14.0` on 2026-11-16 using the TypeSafe skill and configured local credential.
Only the design and relevant source were sent. Requests and responses are retained locally under
`artifacts/reviews/245/`.

The first design review supported the fluent C# surface (0.87), selector identity (0.92), endpoint
isolation (0.88), response ambiguity handling (0.84), and nested coverage (0.96). It identified an
omitted converter-precedence rule (0.12 for whether that rule was explicit); the contract above
now defines it. A source review supported cycle protection (0.93), reference preservation (0.93),
and wire-name lookup (0.96), but was uncertain about converter precedence (0.46). A follow-up using
only the exporter rated original-contract precedence at 0.79 and member precedence at 0.89.

These judgments are advisory. Executable regressions establish converter precedence and nullable
fallback, recursive/polymorphic/dictionary traversal, route/query/body/form/custom input text,
shared-component isolation, renamed JSON member identity, ignored-property rejection, response
selection errors, and strict coverage success/failure. Package consumers exercise the public
surface; CI also publishes and runs the packed HTTP consumer as a Linux NativeAOT executable.
