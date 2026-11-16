# Migrating to 0.14.1

Recompile model and host projects together. Portia is pre-1.0; generated metadata signatures are
replaced without compatibility aliases.

Standard value annotations now execute before request behaviors, guards, and handlers, after
authorization. This includes local calls, queued workers, streams, and asynchronous acceptance.
Previously accepted values can now produce `Validation` outcomes. Review existing annotations and
constructor defaults; `[Required]` validates a value, while `[JsonRequired]` requires JSON presence.
See [contract attributes](contract-attributes.md) for supported rules and portable schema limits.

HTTP nested bodies and MCP requests now share strict JSON constructor/nullability rules. Missing
non-null constructor members without defaults, and explicit nulls for non-null members, fail input
binding. Supply values, declare nullable members, or provide intentional defaults. `JsonRequired`
presence is checked before HTTP body-to-query fallback. Output serialization and persisted event
schema evolution retain their existing options.

Purely local requests no longer require JSON roots. Wire-bound requests, results, typed examples,
and custom HTTP body/response/header types still need explicit `[PortiaJsonContext]` roots.
`PORTIA031` diagnoses unsupported or contradictory annotations. Reflection-dependent
`Range(Type, ...)`, `DefaultValue(Type, ...)`, and resource-backed validation messages require typed
application validation or literal annotations. Referenced contracts fail with actionable
configuration errors rather than constructing reflection-dependent converters.

Configure named HTTP authorization policies with explicit OpenAPI security mappings. Standard
default/fallback authentication schemes require corresponding security definitions. Maps describe
credentials only; they do not replace runtime policies or Portia authorizers.

Schema component identities are now independent of editorial changes. Regenerate clients once;
subsequent description/title/example edits preserve the structural component names.
