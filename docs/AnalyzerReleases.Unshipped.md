; Unshipped analyzer release

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PORTIA002 | Portia | Error | Projector types must be partial for generated event dispatch
PORTIA005 | Portia | Error | Reactor types must be partial for generated event dispatch
PORTIA008 | Portia | Error | A request cannot have more than one registered handler
PORTIA010 | Portia | Error | A request cannot have more than one registered IRequestAuthorizer<>
PORTIA011 | Portia | Error | RequiresPermission references an unknown request property
PORTIA012 | Portia | Warning | IDomainEventUpcaster.EventName references an unknown event name
