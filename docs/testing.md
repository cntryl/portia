# Testing applications

`Cntryl.Portia.Testing` runs your aggregates, requests, projectors, and reactors through Portia's real
lifecycle and leaves most assertions to the test framework you already use. `DomainEvent` equality compares
type and business data and ignores the metadata Portia attaches. Records with collection members still use
the collection's own equality, which is often reference equality. Use `EventAssert.Equal(expected, actual)`
to compare event payloads structurally, including nested records and ordered collections:

```csharp
EventAssert.Equal([new CriteriaRevised(["a", "b"])], scenario.PendingEvents);
```

| Testing | Use | Runs |
|---|---|---|
| An aggregate's decisions | `AggregateScenario<TAggregate>` | The aggregate in memory, with the executor's commit/discard rules |
| A request's lifecycle | `RequestScenario` | Authorization, pipeline behaviors, guards, and the handler |
| A projector | `ProjectorScenario` | `ProjectorRunner` over the given events, committing checkpoints in memory |
| A reactor | `ReactorScenario` | `ReactorRunner` over the given events, recording the requests it sends |
| A store adapter | The conformance suites | See [Projectors and reactors](projectors-and-reactors.md#storage-implementations) |

## Aggregates

Give history as plain events and run an operation the way a handler would. Portia attaches metadata to bare
events and numbers them from the aggregate's current version; an event you seeded yourself with
`DomainEventSeed.Attach` replays unchanged.

```csharp
var scenario = new AggregateScenario<Account>(new Account(id))
    .Given(new Opened(), new Deposited(10));

var result = scenario.WhenCommitOnSuccess(account => account.Withdraw(15));

Assert.Equal(RequestErrorKind.Conflict, result.Error!.Kind);
Assert.Empty(scenario.PendingEvents);
```

`When` applies the returned `AggregateOutcome` as `IAggregateExecutor` does: after `Commit`, what the
operation raised or audited stays in `PendingEvents` and `PendingAudits`, which is exactly what a save would
write, until the next `Given` or `When` saves them as the executor would have; after `Discard`, or an
operation that throws, nothing does, and the instance refuses further operations exactly as in production. A value-returning operation returns its `Result<TOut>`. Operations that
return nothing can be called on `scenario.Aggregate` directly.

For operations that return `Result` or `Result<TOut>`, use `WhenCommitOnSuccess` to apply the common
commit-on-success rule without constructing an `AggregateOutcome` at each call site. Use `When` with an
explicit outcome when a failure must still commit an audit or another record.

## Requests

Build the service provider the way your application composes Portia, then describe one dispatch and what
should have happened. Awaiting runs the request once in a fresh scope and reports every unmet expectation
with the lifecycle it observed.

```csharp
await RequestScenario.For(services)
    .GivenActor(member)
    .When(new RenameAccount(id, "savings"))
    .ExpectAuthorized()
    .ExpectGuardFailed<MaintenanceWindowGuard>(RequestErrorKind.Conflict)
    .ExpectNotHandled();
```

The actor defaults to anonymous. `TestPermissionEvaluator.AllowAll()` and `DenyAll()` stand in for the
application's permission policy. A concurrency conflict is reported as the `Conflict` failure transports return; the bus itself rethrows `EventStreamConcurrencyException` to in-process callers.

Scenarios run only when awaited; `PORTIA107` warns about one left as a statement, which would otherwise pass
without running.

A scenario always dispatches through Portia's own bus, since that is what it observes. An application that
decorates or replaces `IRequestBus` tests that wrapper by resolving its `IRequestBus` and dispatching through it
directly.

## Projectors

A projector writes through your own repository and commits progress through its `IProjectionStore`. Construct
it with `scenario.Store` for progress and a fake or in-memory repository for data, then assert on that data.

```csharp
var accounts = new InMemoryAccountRepository();
var scenario = new ProjectorScenario().Given(
    DomainEventSeed.Attach(new MoneyDeposited(10), accountId, 1),
    DomainEventSeed.Attach(new MoneyDeposited(5), accountId, 2));

await scenario.RunAsync(new AccountProjector(accounts, scenario.Store));

Assert.Equal(15, accounts.Balances[accountId]);
```

Bare events belong to one scenario aggregate; seed events with `DomainEventSeed.Attach` when the projector
depends on specific aggregate IDs. `RunAsync` resumes from the committed checkpoint, as a hosted projector
does, so calling `Given` and `RunAsync` again projects only the new events. The scenario's store cannot roll
back your repository, so a projector that fails mid-batch leaves its partial writes in a fake; test rollback
against the real repository with the projection-store conformance suite.

## Reactors

Construct the reactor with `scenario.Requests` as its `IRequestBus` and a `new InMemoryProjectionCheckpointStore()`
for its progress. Every command it sends is recorded and
succeeds unless scripted with `RespondTo<TRequest>`.

```csharp
var scenario = new ReactorScenario()
    .Given(new UserCreated(userId))
    .RespondTo<SendWelcomeEmail>(Result.Failure(new RequestError(RequestErrorKind.Conflict, "bounced")));

await Assert.ThrowsAsync<ReactionCommandFailedException>(() =>
    scenario.RunAsync(new WelcomeReactor(scenario.Requests)));

Assert.Equal<IRequestBase>([new SendWelcomeEmail(userId)], scenario.SentRequests);
```

`RunAsync` always delivers from the start of the streams, so running twice shows what the reactor does when an
event is redelivered after a failed checkpoint. The recorded bus handles commands only; a reactor that sends a
request with a result or a stream request should be tested through `RequestScenario` against a real provider.

`Given` attaches metadata to the event instances you pass, as raising them would. Construct events per test
rather than sharing one instance across scenarios.

Both component scenarios run a tenant-scoped component (`EventStreamPattern.ForTenant`) bound to the
`"scenario"` test tenant by default. Pass a `TenantId` to either scenario constructor to bind it to a
specific tenant, as hosting would bind it per tenant. For a tenant-scoped component, given events are
placed on that tenant's streams.
