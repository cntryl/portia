; Shipped analyzer releases

## Release 0.10.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
PORTIA200 | PortiaStructure | Warning | Application files declare one type
PORTIA201 | PortiaStructure | Warning | Application filenames identify their type, using OfT for generics
PORTIA202 | PortiaStructure | Warning | Application namespaces follow folders
PORTIA203 | PortiaStructure | Warning | Common and Core use closed feature roles with supported shared Core folders, feature Events, and nested role areas
PORTIA204 | PortiaStructure | Warning | Components and support types belong in their feature role folders; execution guards, action and operation catalogs, aggregate roots and directly owned collaborators, and immutable record values follow their application roles
PORTIA205 | PortiaStructure | Warning | Each request handler handles one command or query
PORTIA206 | PortiaStructure | Warning | Common and Core respect application and infrastructure dependency boundaries
PORTIA207 | PortiaStructure | Warning | Core component registrations belong in one feature registration file
PORTIA208 | PortiaStructure | Warning | Aggregate files over 400 lines should use composition to stay reviewable
PORTIA209 | PortiaStructure | Warning | Common contracts do not declare executable behavior
PORTIA210 | PortiaStructure | Warning | Contracts use feature.name snake_case discriminators with explicit positive versions
PORTIA211 | PortiaStructure | Warning | Command handlers must not be NotImplementedException stubs
PORTIA212 | PortiaStructure | Warning | Queries and reactors must not hydrate aggregates
PORTIA213 | PortiaStructure | Warning | Application projects use Common, Core, Api, or Worker roles

## Release 0.8.0

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
| PORTIA030 | Portia   | Error    | JSON contexts require accessible non-generic concrete declarations and usable options constructors |
| PORTIA100 | Portia   | Warning  | Projectors should not take known effect dependencies                                  |
| PORTIA101 | Portia   | Warning  | Portia components should not use service-location dependencies or ActivatorUtilities   |
| PORTIA102 | Portia   | Warning  | Aggregates must not depend on services                                                |
| PORTIA104 | Portia   | Warning  | Unexpected failures must stay exceptions, not become a failed Result                  |
| PORTIA105 | Portia   | Warning  | Request guards should not take Portia write or dispatch dependencies                  |
| PORTIA106 | Portia   | Warning  | Request authorizers should not take Portia write or dispatch dependencies             |
| PORTIA107 | Portia   | Warning  | Returned request expectations must be observed; discarded augmentation loses assertions         |
