# Migrating to Portia 0.13.0

Portia 0.13.0 removes public members, makes one transport member required, and turns several silent
misconfigurations into errors. Keep the complete `Cntryl.Portia.*` package family on 0.13.0.
Applications upgrading from a version before 0.12.0 must first follow the earlier migration notes.

## Dependencies

`Cntryl.Portia.Cassie` now depends on `Cntryl.Cassie` 0.2.0. Update a direct or central
`Cntryl.Cassie` reference to 0.2.0. Its `CassieCommand.Transaction` is typed as `CassieTransaction`,
and `CassieConnection.CreateCommand()` returns `CassieCommand`, so casts from the `System.Data.Common`
base types are no longer needed in application repositories.

## Removed members

| Removed | Use instead |
| --- | --- |
| `ProjectorRegistration.Run`, `ProjectorRegistration.RunPass` | Hosting, or `ProjectorRunner.RunAsync` with `ProjectorRegistration.Resolve` |
| `ReactionCommandFailedException` | Return the failed `Result` from `SendReactionAsync`; Portia has not thrown this type since 0.9.3 |

## Custom queue adapters

`IQueuedRequest.RetryAsync(TimeSpan, CancellationToken)` no longer has a default implementation.
Every `IQueuedRequest`, including test doubles, must implement it. An adapter whose transport cannot
delay redelivery waits out the delay itself before releasing the reservation; a test double can
delegate to `AbandonAsync`.

## Requests

- A request type is handled through one contract. If a type implements more than one of `IRequest`,
  `IRequest<TOut>`, and `IStreamRequest<TOut>`, register one handler and dispatch through that
  contract. Dispatching through another now throws `InvalidOperationException` naming the handled
  contract, and registering two contracts for one type fails at composition.
- `[RequiresPermission]` must render a non-blank permission. A template whose tokens can produce an
  empty string now fails the request with `InvalidOperationException` instead of reaching the
  evaluator.
- `DispatchStreamAsync` validates its arguments when called. Code that relied on a null argument
  failing only during enumeration sees the `ArgumentNullException` earlier.
- An authorizer that must run after the declared permission and before resource authorizers can
  register at `AuthorizationStage.Permission`. Dashboards keyed on authorization telemetry gain a
  `permission` component and stage.

## Projectors and reactors

- Returning `default(Result)` from a handler now stops the hosted worker immediately with an error
  naming the component. It was previously retried as a transient failure.
- A custom `IProjectionBatch` consumer that called `CommitAsync` twice on a Fitz KV batch now
  receives `InvalidOperationException`.
- Alerts that match `ProcessorFailureException.Stage == Reading` for operational confirmation
  failures should match `Committing`.

## Fleet

- Code that calls `FitzFleetMembership.RunAsync` directly must set `FleetRunOptions.WorkerId`.
- `ReconciliationInterval` and `PartitionStopTimeout` must be at most about 49.7 days.
- Code that catches `FleetPartitionTerminationTimeoutException` from `FleetPartitionRunner.RunAsync`
  should also handle `WorkloadFailureException`: when a workload fails terminally and another
  partition then fails to stop, the workload failure is thrown and carries the timeout in
  `Data["Portia.CleanupException"]`.

## Testing

`ReactorScenario` no longer throws `NotSupportedException` for result-bearing or stream requests.
Script them with `RespondTo<TRequest, TOut>` or `RespondToStream<TRequest, TOut>`; an unscripted one
fails the run with `InvalidOperationException`. The scenario bus records and answers from the script
and runs no authorizer, guard, behavior, or handler; test those with `RequestScenario`.

## Documentation

`docs/request-guards.md` moved to `docs/request-pipeline.md`.
