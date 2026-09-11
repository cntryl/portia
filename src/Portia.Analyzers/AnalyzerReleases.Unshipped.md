; Unshipped analyzer release

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PORTIA100 | Portia | Warning | Projectors should not take known effect dependencies; recognized type ancestry is a best-effort heuristic
PORTIA101 | Portia | Warning | Portia components should not use known service-location dependencies or ActivatorUtilities
PORTIA102 | Portia | Warning | Aggregates must not depend on services
PORTIA103 | Portia | Warning | A type should handle only one request
PORTIA104 | Portia | Warning | Unexpected failures must stay exceptions, not become a failed Result
