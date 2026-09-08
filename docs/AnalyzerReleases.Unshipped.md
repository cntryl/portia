; Unshipped analyzer release

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PORTIA002 | Portia | Error | Projector types must be partial for generated event dispatch
PORTIA005 | Portia | Error | Reactor types must be partial for generated event dispatch
PORTIA011 | Portia | Error | RequiresPermission references an unknown request property
PORTIA012 | Portia | Warning | IJsonDomainEventUpcaster.EventName references an unknown event name
PORTIA025 | Portia | Error | Portia serializer root is not explicitly registered on a PortiaJsonContext
PORTIA013 | Portia | Error | RequiresPermission references a nullable request property
PORTIA015 | Portia | Error | Generated components must have supported accessible non-generic declarations
PORTIA016 | Portia | Error | Unsupported HTTP binding requires a constant route and supported constructor and scalar types
PORTIA017 | Portia | Error | Batch handlers require batch bases and unambiguous event dispatch
PORTIA018 | Portia | Error | Generic component registration requires a matching Portia role
PORTIA019 | Portia | Error | Registration calls must appear at a call site supported by generated interceptors
PORTIA020 | Portia | Error | Transported requests require a valid explicit discriminator
PORTIA021 | Portia | Error | Domain events require a valid explicit discriminator
PORTIA022 | Portia | Error | Request discriminator name and version pairs must be unique
PORTIA023 | Portia | Error | Domain-event discriminator name and version pairs must be unique
PORTIA024 | Portia | Error | Declared request route segments must be safe single segments
