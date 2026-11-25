; Shipped analyzer releases

## Release 0.15.1

### Removed Rules

 Rule ID   | Category        | Severity | Notes
-----------|-----------------|----------|------
 PORTIA208 | PortiaStructure | Warning  | Aggregate size and member counts do not establish a misplaced responsibility.

## Release 0.15.0

### New Rules
 Rule ID   | Category | Severity | Notes
-----------|----------|----------|------
 PORTIA032 | Portia | Error | Internal system requests must be unary request/response commands, not streams.
 PORTIA109 | Portia   | Disabled | Reactors sending complementary create/cancel commands for one resource should be consolidated.
 PORTIA110 | Portia   | Disabled | Reactors can opt in to direct-effect purity guidance without changing default builds.
 PORTIA111 | Portia   | Disabled | Opt-in reactors can only read the trigger and send one or more commands.
 PORTIA214 | PortiaStructure | Warning | Internal system request handlers must be registered by the owning feature.

## Release 0.14.9

### New Rules

 Rule ID   | Category | Severity | Notes
-----------|----------|----------|------
 PORTIA108 | Portia   | Warning  | Registered aggregate event handlers should apply events deterministically; move business validation and expected failures to commands.

## Release 0.14.1

### New Rules

 Rule ID   | Category | Severity | Notes
-----------|----------|----------|-------------------------------------------------------------------------
 PORTIA031 | Portia   | Error    | Reject invalid or reflection-dependent contract attribute declarations.

## Release 0.10.0

### New Rules

 Rule ID   | Category        | Severity | Notes
-----------|-----------------|----------|----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
 PORTIA200 | PortiaStructure | Warning  | Application files declare one type
 PORTIA201 | PortiaStructure | Warning  | Application filenames identify their type, using OfT for generics
 PORTIA202 | PortiaStructure | Warning  | Application namespaces follow folders
 PORTIA203 | PortiaStructure | Warning  | Common and Core use closed feature roles with supported shared Core folders, feature Events, and nested role areas
 PORTIA204 | PortiaStructure | Warning  | Components belong in their feature role folders; Api/Worker contain composition, and each entity in Aggregates/Entities has exactly one same-feature aggregate owner
 PORTIA205 | PortiaStructure | Warning  | Each request handler handles one command or query
 PORTIA206 | PortiaStructure | Warning  | Application projects respect application and infrastructure dependency boundaries; Api and Worker do not reference each other
 PORTIA207 | PortiaStructure | Warning  | Core component registrations belong in one feature registration file
 PORTIA208 | PortiaStructure | Warning  | Aggregate files over 400 lines start review with invariants and their owners, then consider composition or child entities
 PORTIA209 | PortiaStructure | Warning  | Common contracts do not declare executable behavior
 PORTIA210 | PortiaStructure | Warning  | Contracts use feature.name snake_case discriminators with explicit positive versions
 PORTIA211 | PortiaStructure | Warning  | Command handlers must not be NotImplementedException stubs
 PORTIA212 | PortiaStructure | Warning  | Queries and reactors must not reach aggregate hydration through statically bound calls in the same compilation
 PORTIA213 | PortiaStructure | Warning  | Application projects use Common, Core, Api, or Worker roles

## Release 0.8.0

### New Rules

| Rule ID   | Category | Severity | Notes                                                                                              |
|-----------|----------|----------|----------------------------------------------------------------------------------------------------|
| PORTIA002 | Portia   | Error    | Projector types must be partial for generated event dispatch; code fix adds `partial`              |
| PORTIA005 | Portia   | Error    | Reactor types must be partial for generated event dispatch; code fix adds `partial`                |
| PORTIA011 | Portia   | Error    | RequiresPermission references an unknown request property                                          |
| PORTIA012 | Portia   | Warning  | IJsonDomainEventUpcaster.EventName references an unknown event name                                |
| PORTIA013 | Portia   | Error    | RequiresPermission references a nullable request property                                          |
| PORTIA015 | Portia   | Error    | Generated components must have supported accessible non-generic declarations                       |
| PORTIA017 | Portia   | Error    | Batch handlers require the corresponding batch processor base                                      |
| PORTIA018 | Portia   | Error    | Generic component registration requires a matching Portia role                                     |
| PORTIA019 | Portia   | Error    | Registration calls must appear at a call site supported by generated interceptors                  |
| PORTIA020 | Portia   | Error    | Transported requests require a valid explicit discriminator                                        |
| PORTIA021 | Portia   | Error    | Domain events require a valid explicit discriminator                                               |
| PORTIA022 | Portia   | Error    | Request discriminator name and version pairs must be unique                                        |
| PORTIA023 | Portia   | Error    | Domain-event discriminator name and version pairs must be unique                                   |
| PORTIA024 | Portia   | Error    | Declared request route segments must be safe single segments                                       |
| PORTIA025 | Portia   | Error    | Portia serializer root is not explicitly registered on a PortiaJsonContext                         |
| PORTIA028 | Portia   | Error    | A processor cannot select single and batch handling for the same event                             |
| PORTIA029 | Portia   | Error    | Request transport IDs must be nonblank ASCII identifiers                                           |
| PORTIA030 | Portia   | Error    | JSON contexts require accessible non-generic concrete declarations and usable options constructors |
| PORTIA100 | Portia   | Warning  | Projectors should not take known effect dependencies                                               |
| PORTIA101 | Portia   | Warning  | Portia components should not use service-location dependencies or ActivatorUtilities               |
| PORTIA102 | Portia   | Warning  | Aggregates must not depend on services                                                             |
| PORTIA104 | Portia   | Warning  | Unexpected failures must stay exceptions, not become a failed Result                               |
| PORTIA105 | Portia   | Warning  | Request guards should not take Portia write or dispatch dependencies                               |
| PORTIA106 | Portia   | Warning  | Request authorizers should not take Portia write or dispatch dependencies                          |
| PORTIA107 | Portia   | Warning  | Returned request expectations must be observed; discarded augmentation loses assertions            |
