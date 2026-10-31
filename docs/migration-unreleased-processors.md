# Migrating to the unreleased processor contracts

These changes apply to the unreleased processor contracts. Update processor
implementations before enabling the application structure diagnostics on an existing application.

## Reactor outcomes must be returned

`IReactorHandler<TEvent>.HandleAsync` now returns `ValueTask<Result>`, rather than `ValueTask`.
Batch reactor handlers also return a result. `SendReactionAsync` returns the command outcome;
a failed command no longer throws `ReactionCommandFailedException`.

When the reaction consists of sending one command, return that operation directly:

```csharp
public ValueTask<Result> HandleAsync(IReactorContext<OrderPlaced> context, CancellationToken ct)
    => requests.SendReactionAsync(new ReserveInventory(context.Trigger.OrderId), context, ct);
```

For a handler with additional work, propagate failure before continuing:

```csharp
var result = await requests.SendReactionAsync(command, context, ct).ConfigureAwait(false);
if (!result.IsSuccess)
    return result;

// Continue only after the command succeeded.
return Result.Success;
```

Do not migrate by awaiting `SendReactionAsync` and then unconditionally returning `Result.Success`.
That compiles but discards a failed command outcome and can advance the reactor checkpoint.
Review every `SendReactionAsync` call, including calls wrapped by application helpers. Merely changing
the return type does not establish correct outcome propagation.

A failed processor result means processing is incomplete and prevents checkpoint advancement.
An expected business rejection still needs an explicit domain outcome and resolution: record the
rejection, compensate, or flag intervention as appropriate. A successful advisory cross-aggregate
read does not replace validation of the authoritative participant outcome. Preserve pending and
unknown states until that outcome is resolved.

## Projectors receive a typed context

`IProjectorHandler<TEvent>.HandleAsync` now takes `IProjectorContext<TEvent>` and returns
`ValueTask<Result>`. Read the event through `context.Trigger`, the source record through
`context.Source`, and shared batch metadata through `context.Batch`. Move the old event parameter's
uses to `context.Trigger`; keep projection writes inside the existing repository transaction.

Overrides of `ProjectEventAsync` return `ValueTask<Result>`. Return a successful result only after
the event was applied successfully. Propagate declared failures instead of discarding them.
Generated dispatch is still the preferred implementation for partial projectors and reactors.

## Processor exceptions carry failure context

Processor passes now throw `ProcessorFailureException` with the original thrown exception in
`InnerException`. Tests that asserted the original exception directly should assert the wrapper
and then check `InnerException`. The wrapper also identifies the processor, failure stage, source
range, and last confirmed checkpoint. A declared `Result.Failure` is available through `Error`;
it need not have an inner exception. Do not catch the wrapper and report successful processing.

## Dependency versions

The dependency floor includes Microsoft Extensions and ASP.NET Core packages at **10.0.14**
and Microsoft.OpenApi at **2.13.0**. Consumers pinning 10.0.13 or Microsoft.OpenApi 2.12.2 can receive
`NU1605` downgrade errors. Update matching direct references or central package versions to at least
the required versions; do not suppress the downgrade diagnostic. Keep the Portia package family on
the same candidate or release version.

## Stage analyzer adoption

`PortiaProjectStructure` is the master opt-in. Individual rules can be enabled or disabled using
standard Roslyn `.editorconfig` severities; a separate baseline file is unnecessary. For example:

```ini
[*.cs]
dotnet_analyzer_diagnostic.category-PortiaStructure.severity = none
dotnet_diagnostic.PORTIA211.severity = warning
dotnet_diagnostic.PORTIA212.severity = warning
dotnet_diagnostic.PORTIA202.severity = warning
```

See [application structure](project-structure.md) for folder responsibilities and the bulk adoption
order. PORTIA202 fixes update user-authored references; source-generated references are regenerated
from their inputs rather than edited.
