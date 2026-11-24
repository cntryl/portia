; Unshipped analyzer release
;
; Rules are listed in ID order. The 0xx band reports contracts the compiler must enforce for a
; build to be sound; the 1xx band reports general design warnings and the 2xx band reports
; opt-in application structure warnings.
;
; Retired IDs, never to be reused: PORTIA001, PORTIA003, PORTIA004, PORTIA006, PORTIA007,
; PORTIA008, PORTIA009, PORTIA010, PORTIA014, PORTIA103. Reusing one would make an old suppression in a
; consumer's code silently apply to an unrelated rule.

### New Rules

 Rule ID   | Category | Severity | Notes
-----------|----------|----------|------
 PORTIA032 | Portia | Error | Internal system requests must be unary request/response commands, not streams.
 PORTIA109 | Portia   | Disabled | Reactors sending complementary create/cancel commands for one resource should be consolidated.
 PORTIA110 | Portia   | Disabled | Reactors can opt in to direct-effect purity guidance without changing default builds.
 PORTIA111 | Portia   | Disabled | Opt-in reactors can only read the trigger and send one or more commands.
 PORTIA214 | PortiaStructure | Warning | Internal system request handlers must be registered by the owning feature.
