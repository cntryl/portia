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

The Microsoft OpenAPI schema generator receives the same frozen options, resolver chain,
converters, property metadata, and naming policy. Document generation does not add a
Portia-owned resolver or require reflection fallback; the reflection-disabled consumer exercises
automatic application interception without manual OpenAPI calls.

At host startup Portia freezes the configured options and resolves metadata for every generated
registration before a Fitz hosted service can connect. All missing roots are reported together,
sorted by full name. A non-hosted DI consumer receives the same check when it first resolves the
JSON options. Portia does not emit metadata or take serialization ownership from the application.
Portia's runtime
packages set `IsAotCompatible=true`, are built with the resulting trim/AOT analyzers, and are
exercised by CoreCLR tests with JSON reflection disabled. No native `PublishAot` binary is built
or executed, so analyzer-clean and reflection-disabled evidence is not a claim that a native
artifact was executed.

See [scope](scope.md).
