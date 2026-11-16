# Migrating to 0.14.0

Upgrade all `Cntryl.Portia.*` packages together and recompile. `Parameter<T>` now has an optional
fourth `description` parameter; its previous three-argument CLR signature is removed. Existing
source calls continue to compile through the new signature. There is no compatibility overload.

Replace application-owned parameter/response/schema description transformers with the explicit
[OpenAPI description APIs](openapi-descriptions.md). Keep operation summaries and descriptions
on ASP.NET's `WithSummary` / `WithDescription`. These APIs change documentation only.

Endpoint descriptions override type property descriptions, which override shared wire-name
member descriptions. Use CLR selectors for endpoint/type properties and exact wire names for
shared defaults. For custom binders, put descriptions on `Parameter<T>` declarations. A success
response with multiple 2xx statuses needs a status-specific `DescribeResponse` call.

Coverage enforcement is opt-in. Before enabling `RequireDescriptions`, register object and
property descriptions and generate both documents in CI. The flag validates document generation;
it does not make ordinary compilation generate the document. The failure lists all missing
locations, including ordinary ASP.NET components in the same document.
