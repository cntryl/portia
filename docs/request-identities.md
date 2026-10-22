# Typed logical request identity

`IIdentifiedRequest` maps a required request value to Portia's logical
`RequestMetadata.RequestId`. The value can identify a creation, submission or
decision, or be a dedicated `CommandId` for a repeated mutation. Keep it an
ordinary typed argument; an explicit interface implementation avoids adding a
second JSON property:

```csharp
[Discriminator("orders.rename", 1)]
public sealed record RenameOrder(Uuid OrderId, Uuid CommandId, ulong ExpectedRevision,
    string Name) : IRequest, ICallable, IIdentifiedRequest
{
    Uuid IIdentifiedRequest.RequestId => CommandId;
}

app.MapPortiaPut<RenameOrder>("/orders/{order_id}/revisions/{command_id}");
```

The route supplies `CommandId`; the body need not repeat it. A supplied JSON or
eligible form duplicate must agree with the routed value after typed binding.
JSON naming, per-property converters and case-insensitive matching follow the
application's options. Every matching duplicate is checked, so a later matching
value cannot hide an earlier conflicting value. Route-only mutations also inspect
supplied bodies; form reads retain the existing antiforgery requirements. A
missing required route does not match an endpoint; malformed or empty typed
identities fail validation. No missing client value becomes an accepted
server-generated operation identity.

Direct send helpers, HTTP and MCP dispatch, outgoing queue/RPC/notice/schedule
helpers and child sends preserve the typed identity. Queue acceptance returns
that same identity, and worker redelivery creates a fresh execution while
preserving carried logical metadata. `RequestMetadata.FromRequest(request, parent)`
also preserves a parent's correlation and causation. Serializing or receiving
metadata that contradicts the typed value fails before handler work; it is never
silently rewritten into another accepted operation.

`RequestDispatchContext.BindRequest(request)` returns either validation or an
immutable copy of the existing receiver execution with its logical identity
bound. It preserves the actor snapshot, invocation, `ExecutionId` and `StartedAt`.
An explicitly propagated request identity must already agree. The normal bus
still authorizes before invoking the handler. A custom transport can bind before
accepting work and carry `binding.Value.Metadata`; it should create a receiver
execution for each delivery. Requests without `IIdentifiedRequest` retain their
existing identity behavior, including receiver-owned context factories.

This contract propagates identity; it does not deduplicate business operations.
The owning aggregate must authorize current access, compare original actor,
kind, owner, lifecycle, authored intent and expected revision against accepted
facts, and distinguish an exact retry from a changed operation. Do not add
receipt-only events or accumulating receipt dictionaries to obtain this behavior.
Transport markers and authorization still control exposure; this interface does
not make a request callable from MCP or another transport.

Reactor callers derive effect identities from the source event, effect kind,
target and lifecycle, then put that value in the typed command. Repeated reaction
delivery preserves it while each child execution remains distinct. A recurring
generator needs a business-window-specific operation identity; reusing one key
for different occurrences would describe the wrong operation.

Scheduling a typed operation preserves that fixed identity on every firing, so
firings represent repeated delivery of the same operation. `EnsureAsync` also
uses the typed key and keeps its stable definition bytes. For recurring business
work, schedule a generator without a fixed operation identity and have it create
typed commands with one key per business window. Portia never invents or rewrites
a typed command's business identity from a firing attempt.
