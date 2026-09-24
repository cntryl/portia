; Unshipped analyzer release
;
; Rules are listed in ID order. The 0xx band reports contracts the compiler must enforce for a
; build to be sound; the 1xx band reports design rules that are warnings by choice.
;
; Retired IDs, never to be reused: PORTIA001, PORTIA003, PORTIA004, PORTIA006, PORTIA007,
; PORTIA008, PORTIA009, PORTIA010, PORTIA014, PORTIA103. Reusing one would make an old suppression in a
; consumer's code silently apply to an unrelated rule.

### New Rules

| Rule ID   | Category | Severity | Notes                                                                                 |
|-----------|----------|----------|---------------------------------------------------------------------------------------|
| PORTIA002 | Portia   | Error    | Projector types must be partial for generated event dispatch; code fix adds `partial` |
| PORTIA005 | Portia   | Error    | Reactor types must be partial for generated event dispatch; code fix adds `partial`   |
| PORTIA011 | Portia   | Error    | RequiresPermission references an unknown request property                             |
| PORTIA012 | Portia   | Warning  | IJsonDomainEventUpcaster.EventName references an unknown event name                   |
| PORTIA013 | Portia   | Error    | RequiresPermission references a nullable request property                             |
| PORTIA015 | Portia   | Error    | Generated components must have supported accessible non-generic declarations          |
| PORTIA017 | Portia   | Error    | Batch handlers require the corresponding batch processor base                         |
| PORTIA018 | Portia   | Error    | Generic component registration requires a matching Portia role                        |
| PORTIA019 | Portia   | Error    | Registration calls must appear at a call site supported by generated interceptors     |
| PORTIA020 | Portia   | Error    | Transported requests require a valid explicit discriminator                           |
| PORTIA021 | Portia   | Error    | Domain events require a valid explicit discriminator                                  |
| PORTIA022 | Portia   | Error    | Request discriminator name and version pairs must be unique                           |
| PORTIA023 | Portia   | Error    | Domain-event discriminator name and version pairs must be unique                      |
| PORTIA024 | Portia   | Error    | Declared request route segments must be safe single segments                          |
| PORTIA025 | Portia   | Error    | Portia serializer root is not explicitly registered on a PortiaJsonContext            |
| PORTIA028 | Portia   | Error    | A processor cannot select single and batch handling for the same event                |
| PORTIA029 | Portia   | Error    | Request transport IDs must be nonblank ASCII identifiers                              |
| PORTIA100 | Portia   | Warning  | Projectors should not take known effect dependencies                                  |
| PORTIA101 | Portia   | Warning  | Portia components should not use service-location dependencies or ActivatorUtilities   |
| PORTIA102 | Portia   | Warning  | Aggregates must not depend on services                                                |
| PORTIA104 | Portia   | Warning  | Unexpected failures must stay exceptions, not become a failed Result                  |
| PORTIA105 | Portia   | Warning  | Request guards should not take Portia write or dispatch dependencies                  |
| PORTIA106 | Portia   | Warning  | Request authorizers should not take Portia write or dispatch dependencies             |
| PORTIA107 | Portia   | Warning  | Test scenarios must be awaited; a discarded scenario runs and asserts nothing         |
