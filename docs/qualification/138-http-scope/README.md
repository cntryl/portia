# HTTP cancellation scope disposal repaired; stdio release gate remains blocked

This follow-up supersedes the HTTP cleanup finding in
[the initial cancellation investigation](../138-cancellation-blocker/README.md).
It does not resolve the SDK 2.2.0 stdio client cancellation defect.

The stateless HTTP SDK assigns ASP.NET request services and sets `ScopeRequests`
to false when constructing each server instance. Configuring `McpServerOptions`
earlier therefore cannot establish operation scope ownership. Portia now uses
`HttpServerTransportOptions.ConfigureSessionOptions`, called after that override,
to enable asynchronous operation scopes. The application's existing callback
still receives the original HttpContext and cancellation token and can configure
server instructions and other options. Operation scopes are enabled after it.

Handlers retain authorization through the HTTP principal and IRequestBus dispatch.
The SDK owns the asynchronous scope and disposes it when the handler exits, even
when the HTTP response stream is aborted. No new span, exporter, retry, reflection,
public signature, or dependency version was introduced.

## Verification

| Check | Result |
| --- | --- |
| HTTP tools/resource reads/prompt gets, caller cancellation and deadlines | 6 passed |
| Application callback compatibility and enforced operation scope | 1 passed |
| Existing MCP suite without the separately run lifecycle matrix | 96 passed |
| Full MCP suite, including every known SDK failure and the new compatibility test | 115 passed, 9 failed, zero skipped |
| Full core unit suite | 1,134 passed, zero skipped |
| Real pinned broker and storage integration categories | 24 passed, zero skipped |
| Locked solution restore | Passed |
| Complete Release build | Passed, zero warnings/errors |
| Full solution formatting and whitespace checks | Passed |

The nine remaining failures are the six ordinary-cancellation cases on a minimal
SDK stdio server (three primitives × two protocol revisions) and the same three
operations through Portia stdio. Explicit-notification controls pass; they remain
separate diagnostics and do not replace ordinary cancellation.

NuGet's published version list and GitHub releases both stop at SDK 2.2.0. No
newer stable version is currently available to qualify. The release gate remains
blocked; no dependent batch has started, and no merge, tracker completion, tag,
or publication is justified by these local results.

## Review and provenance

The callback compatibility test prevents accidentally discarding application
configuration. Existing application-parity tests cover authorization, tenant
isolation, fresh scopes, concurrency, and failure paths. The real handler fixture
still requires cancellation to be observed and a dedicated asynchronous wait
scope to dispose. Diagnostic tool notifications identify the waiting request by
its arguments rather than selecting a concurrent polling request ID.

`source-sha256.txt` records the exact edited source inputs; `environment.txt`
records the base commit, local runtime, and isolated integration endpoints.
The test logs and TRX outcomes are retained beside this report. These are local
qualification results on the existing transport worktree, not exact merged-commit
CI, CodeQL, packed-consumer, or Linux NativeAOT release proof.
