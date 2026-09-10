; Unshipped analyzer release

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
PORTIA100 | Portia | Warning | Projectors must not take a dispatch or effect dependency
PORTIA101 | Portia | Warning | Portia components must not resolve services from the container
PORTIA102 | Portia | Warning | Aggregates must not depend on services
PORTIA103 | Portia | Warning | A type should handle only one request
PORTIA104 | Portia | Warning | Unexpected failures must stay exceptions, not become a failed Result
