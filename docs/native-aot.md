# NativeAOT and trimming

Portia-owned runtime paths use generated catalogs and `System.Text.Json` metadata rather than
runtime reflection. Each application or feature module owns a small JSON context for the public
contracts it defines:

```csharp
[PortiaJsonContext]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DepositAccount))]
[JsonSerializable(typeof(Deposited))]
internal sealed partial class AccountsJsonContext : JsonSerializerContext;
```

Transported requests and durable events use stable discriminators independent of CLR names.
Request routing is a separate concern:

```csharp
[Discriminator("accounts.deposit")]
[RequestRoute("banking", "accounts", "*", "deposit")]
public sealed record DepositAccount(Uuid Id, int Amount) : IRequest, IQueuable;

[Discriminator("accounts.deposited", 2)]
public sealed record Deposited(int Amount, string Currency) : DomainEvent;
```

Configure shared JSON behavior before building the service provider:

```csharp
services.AddPortia()
    .ConfigureJson(options => options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
    .AddRequestHandler<DepositAccountHandler>();
```

Portia composes generated module contexts with its built-in context and freezes the options. Do
not replace the generated resolver chain or depend on reflection fallback. `PORTIA025` requires
every request, unary result, streaming item, generated HTTP body-member, and domain-event root to
appear explicitly in `[JsonSerializable]`; nested object-graph types remain System.Text.Json's
responsibility. Its code fix adds the attribute to the only context, prefers a unique context in
the root's namespace, or asks which context owns the root; when none exists it creates
`PortiaJsonContext.cs` with Web defaults and `SnakeCaseLower`.

`PORTIA030` diagnoses contexts that generated code cannot use: private/file-local contexts or enclosing
types, generic shapes, abstract/wrong-base types, or inaccessible/missing options constructors. Public
and internal contexts remain supported, including roots across partial files. A source-generated partial
context needs a `JsonSerializable` root; a manually implemented context needs an accessible constructor
accepting `JsonSerializerOptions`. Code fixes select usable hand-written contexts, including an empty
partial context that becomes usable when the fix adds its first root.

JSON factory identifiers now encode the full assembly and context identity injectively. Generated names
change when recompiling a module; reference composition continues to use its advertised
`PortiaJsonRootAttribute` factory, so already compiled factory metadata remains supported. Applications
should use generated registration rather than naming generated factory classes themselves.

HTTP mappings need concrete direct calls. `PORTIA016` diagnoses mapping method groups and expression
trees before startup. Valid extension calls with static imports remain supported; bare unqualified
extension-method groups are rejected by C# itself. HTTP generator assets ship in `Cntryl.Portia.AspNetCore`.

The Microsoft OpenAPI schema generator receives the same frozen options, resolver chain,
converters, property metadata, and naming policy. Document generation does not add a
Portia-owned resolver or require reflection fallback; the reflection-disabled consumer exercises
automatic application interception without manual OpenAPI calls.
The asynchronous `v1` document endpoints use the same Microsoft pipeline and frozen metadata for
the served JSON and YAML; document-generation failures do not block application startup.

At host startup Portia freezes the configured options and resolves metadata for every generated
registration before a Fitz hosted service can connect. All missing roots are reported together,
sorted by full name. A non-hosted DI consumer receives the same check when it first resolves the
JSON options. Portia does not emit metadata or take serialization ownership from the application.
Portia's runtime packages set `IsAotCompatible=true`, are built with the resulting trim/AOT
analyzers, and are exercised by CoreCLR tests with JSON reflection disabled. CI also packs the
shipping packages, restores them into an external consumer, publishes that consumer as a native
`linux-x64` executable with `PublishAot=true`, and executes the binary. This is the supported
runtime proof for the Portia ASP.NET Core path; applications must still prove AOT compatibility of
their own dependencies and platform-specific deployment choices.

See [scope](scope.md).
