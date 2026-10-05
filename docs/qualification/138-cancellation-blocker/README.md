# Current disposition: upstream cancellation cases temporarily skipped

The nine ordinary stdio token-cancellation cases are temporarily skipped at the
user's direction while SDK 2.2.0 is affected by upstream
[issue #1365](https://github.com/modelcontextprotocol/csharp-sdk/issues/1365).
Each skipped data row names that issue and requires re-enabling after a verified
SDK fix. HTTP cancellation, explicit matched-ID notification controls, deadlines,
and async scope disposal assertions remain active. A green run with these skips
does not establish ordinary stdio token-cancellation support.

The report below is retained as historical investigation evidence; its prohibition
on skipping these upstream cases is superseded by this disposition. The HTTP
scope-disposal issue described below has since been repaired on PR #147.

---

# 0.7.0 cancellation qualification: release blocked

Batch 1 has not passed qualification. Do not merge it, advance dependent batches,
publish 0.7.0, or mark #135/#136 complete on this evidence.

This is a local investigation of the existing uncommitted transport/MCP worktree,
based on commit `14531de`. `source-sha256.txt` identifies the actual tested files;
the base commit alone does not identify this implementation. There is no final
PR-head, merged-commit, CI, CodeQL, or publication proof.

## Findings

The unchanged ModelContextProtocol SDK 2.2.0 cancels the client await but does not
send `notifications/cancelled` in the reproduced ordinary token-cancellation
cases. Tools, resource reads, and prompt gets fail on a minimal SDK stdio server
without Portia dispatch, deadlines, or telemetry. Both `2025-11-25` initialization
and `2026-07-28` discovery reproduce the failure.

Controls explicitly send a cancellation notification using the observed request
ID. They require the handler to observe cancellation and its real asynchronous
DI scope to dispose. These controls isolate ingress and downstream propagation;
they do not satisfy the ordinary caller-cancellation release requirement.

The SDK's [pinned source](https://github.com/modelcontextprotocol/csharp-sdk/blob/6fa3825973949a9c4f0cd8af344e15a8db09dc35/src/ModelContextProtocol.Core/McpSessionHandler.cs)
registers notification sending before awaiting `tcs.Task.WaitAsync(token)`, then
disposes that registration when the await unwinds. The independent reproduction
in `race-reproduction` uses that registration/await/disposal pattern and records
0 of 100 notification callbacks. This supports a callback-ordering diagnosis:
cancellation unwinds the await and removes the earlier registration before it
runs. The retained wire evidence separately establishes the missing notification.
An explicitly awaited notification reaches the SDK server and cancels its handler.

Portia's matched-notification stdio controls also trace cancellation through MCP
ingress, IRequestBus dispatch, the waiting handler, and asynchronous scope disposal.
They retain the existing exporter assertions for spans, metrics, fault logs,
correlation, and span budget.

Strengthening the fixture to require actual scope disposal uncovered another gap:
HTTP caller-cancellation handlers observe cancellation, but their invocation
scopes do not dispose within the bounded polling interval. Deadline cases and
explicit-notification stdio controls exercise separate paths. Enabling the SDK's
per-request scope option in a temporary HTTP diagnostic did not remedy this gap;
that override is not retained in the regression implementation.

## Final local results

The 27-case lifecycle matrix reports **15 passed, 12 failed, zero skipped**:

| Cases | Passed | Failed | Meaning |
| --- | ---: | ---: | --- |
| Minimal SDK ordinary cancellation, three primitives × two revisions | 0 | 6 | Handler cancellation and scope disposal absent before timeout |
| Minimal SDK matched-notification controls | 6 | 0 | SDK ingress, handler token, and actual async scope disposal work |
| Portia stdio ordinary cancellation | 0 | 3 | SDK token cancellation emits no observed notification |
| Portia HTTP ordinary cancellation | 0 | 3 | Handler observes cancellation; scope disposal remains incomplete |
| Portia HTTP and stdio deadlines | 6 | 0 | Separate deadline path passes, including scope disposal |
| Portia stdio matched-notification controls | 3 | 0 | Handler cancellation, scope disposal, and exported telemetry pass |

## Regression design and review

- Original ordinary caller-cancellation cases remain required, with no skips or
  deadline substitution. Child processes use the stable SDK 2.2.0.
- Minimal SDK tests cover each primitive on both initialization paths, with
  separate explicit-notification controls and real scoped IAsyncDisposable probes.
- A dedicated wait-scope probe records only suspended reads; polling reads do not
  change the existing application-parity scope counts.
- Request IDs come from server ingress. Tool controls identify the waiting request
  by its actual `wait` argument, avoiding concurrent state-poll request IDs.
- Ingress recording uses a lock and compact JSON; protocol stdout stays untouched.
  Client trace logs contain only the controlled fixture workload.
- Polling is bounded and errors name the evidence directory. Local cancellation
  completion alone never establishes handler cancellation or disposal.
- Files written during process shutdown after a failed assertion do not prove
  request-bound cleanup. Use TRX outcomes and ingress records together.
- Jev was not used: no authenticated TypeSafe API key was available in the process.

## Commands and artifacts

Run from the transport worktree:

```sh
dotnet restore test/Portia.McpTests/Portia.McpTests.csproj --locked-mode
dotnet test test/Portia.McpTests --no-restore -c Release \
  --filter FullyQualifiedName~McpLifecycleQualificationTests \
  -m:1 /nodeReuse:false --logger 'trx;LogFileName=cancellation.trx'
# Run the independent reproduction outside repository build settings.
probe_dir=$(mktemp -d /tmp/portia-callback-probe.XXXXXX)
cp docs/qualification/138-cancellation-blocker/race-reproduction/* "$probe_dir/"
dotnet run --project "$probe_dir/Race.csproj" -c Release
```

`environment.txt` records the branch, base commit, OS, SDK, and runtime.
`source-sha256.txt` records the actual implementation inputs. The SDK package's
repository metadata pins source commit `6fa3825973949a9c4f0cd8af344e15a8db09dc35`.
Logs, TRX results, and compressed protocol evidence are retained beside this report.
The related schema, telemetry, application-parity, and explicit-notification
control suite passed 28 tests with zero skips. This is focused local evidence,
not full release qualification.

## Required next step

Resolve ordinary cancellation with the unchanged stable SDK contract and prove
both handler cancellation and scope disposal. A server adapter cannot observe a
client cancellation for which no protocol message arrives. SDK upgrades, package
substitution, explicit-notification controls, and deadline expiration cannot turn
these ordinary-cancellation regressions into release qualification. Separately
resolve the HTTP caller-cancellation disposal failure. Then rerun Batch 1's full
checks and perform the planned serial PR delivery gates.
