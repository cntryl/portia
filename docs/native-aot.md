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
not replace the generated resolver chain or depend on reflection fallback. The accepted proof
level is analyzer-clean runtime packages plus CoreCLR tests with JSON reflection disabled; it does
not claim that a native executable was published or executed.

See [known limitations](known-limitations.md).
