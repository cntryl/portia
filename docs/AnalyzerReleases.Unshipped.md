; Unshipped analyzer release

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PORTIA002 | Portia | Error | Projector types must be partial for generated event dispatch
PORTIA005 | Portia | Error | Reactor types must be partial for generated event dispatch
PORTIA008 | Portia | Error | A request cannot have more than one registered handler
PORTIA010 | Portia | Error | A request cannot have more than one registered IRequestAuthorizer<>
PORTIA011 | Portia | Error | RequiresPermission references an unknown request property
PORTIA012 | Portia | Warning | IJsonDomainEventUpcaster.EventName references an unknown event name
PORTIA013 | Portia | Error | RequiresPermission references a nullable request property
PORTIA014 | Portia | Error | Modules must be accessible non-generic top-level partial classes
PORTIA015 | Portia | Error | Generated components must have supported accessible non-generic declarations
