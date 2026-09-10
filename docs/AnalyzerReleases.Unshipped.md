; Unshipped analyzer release
;
; Rules are listed in ID order. The 0xx band reports contracts the compiler must enforce for a
; build to be sound; the 1xx band reports design rules that are warnings by choice.
;
; Retired IDs, never to be reused: PORTIA001, PORTIA003, PORTIA004, PORTIA006, PORTIA007,
; PORTIA008, PORTIA009, PORTIA010, PORTIA014. Reusing one would make an old suppression in a
; consumer's code silently apply to an unrelated rule.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PORTIA002 | Portia | Error | Projector types must be partial for generated event dispatch; code fix adds `partial`
PORTIA005 | Portia | Error | Reactor types must be partial for generated event dispatch; code fix adds `partial`
PORTIA011 | Portia | Error | RequiresPermission references an unknown request property
PORTIA012 | Portia | Warning | IJsonDomainEventUpcaster.EventName references an unknown event name
PORTIA013 | Portia | Error | RequiresPermission references a nullable request property
PORTIA015 | Portia | Error | Generated components must have supported accessible non-generic declarations
PORTIA016 | Portia | Error | Unsupported HTTP binding requires a constant route and supported constructor and scalar types
PORTIA017 | Portia | Error | Batch handlers require the corresponding batch processor base
PORTIA018 | Portia | Error | Generic component registration requires a matching Portia role
PORTIA019 | Portia | Error | Registration calls must appear at a call site supported by generated interceptors
PORTIA020 | Portia | Error | Transported requests require a valid explicit discriminator
PORTIA021 | Portia | Error | Domain events require a valid explicit discriminator
PORTIA022 | Portia | Error | Request discriminator name and version pairs must be unique
PORTIA023 | Portia | Error | Domain-event discriminator name and version pairs must be unique
PORTIA024 | Portia | Error | Declared request route segments must be safe single segments
PORTIA025 | Portia | Error | Portia serializer root is not explicitly registered on a PortiaJsonContext
PORTIA026 | Portia | Error | Optional HTTP route tokens must use query parameters or separate endpoints
PORTIA027 | Portia | Error | Visible Portia HTTP mappings must have unique camel-cased operation IDs
PORTIA028 | Portia | Error | A processor cannot select single and batch handling for the same event
