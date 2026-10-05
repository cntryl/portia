# HTTP cancellation scope ownership

The stateless HTTP SDK sets `ScopeRequests` to false while constructing the
server. Portia enables operation scopes through `ConfigureSessionOptions`, after
that override, while preserving the application's callback and its arguments.
The SDK disposes each asynchronous scope when its handler exits, including when
the HTTP response is aborted.

`McpLifecycleQualificationTests` covers callback compatibility, HTTP cancellation,
deadlines, and asynchronous scope disposal for tools, resources, and prompts.
The separate [stdio SDK limitation](../138-cancellation-blocker/README.md) remains.
