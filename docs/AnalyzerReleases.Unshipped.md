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

Rule ID | Category | Severity | Notes
--------|----------|----------|------
PORTIA200 | Portia | Warning | Application files declare one type
PORTIA201 | Portia | Warning | Application filenames identify their type, using OfT for generics
PORTIA202 | Portia | Warning | Application namespaces follow folders
PORTIA203 | Portia | Warning | Common and Core use closed feature folders
PORTIA204 | Portia | Warning | Known component roles belong in their feature role folders
PORTIA205 | Portia | Warning | Each request handler handles one command or query
PORTIA206 | Portia | Warning | Common and Core respect application and infrastructure dependency boundaries
PORTIA207 | Portia | Warning | Core component registrations belong in one feature registration file
PORTIA208 | Portia | Warning | Aggregate files over 400 lines should use composition to stay reviewable
PORTIA209 | Portia | Warning | Common contracts do not declare executable behavior
PORTIA210 | Portia | Warning | Contracts use feature.name snake_case discriminators with explicit positive versions
PORTIA211 | Portia | Warning | Command handlers must not be NotImplementedException stubs
PORTIA212 | Portia | Warning | Queries and reactors must not hydrate aggregates
PORTIA213 | Portia | Warning | Application projects use Common, Core, Api, or Worker roles
