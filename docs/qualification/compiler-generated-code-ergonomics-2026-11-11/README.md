# Generated-code ergonomics review

This review assesses generated source from both the combined compiler fixture and the
`Portia.Accounts` example against the reviewability, shared structure, clear flow, and source-size
guidance in [`docs/project-structure.md`](../../project-structure.md#generated-code). It is source
inspection, not a user study or a claim about every Portia application.

## Evidence

The campaign ran from source commit `b81b8185bf59211aa4f751845d7c8c3d9779a697` on .NET 11.0.0,
Roslyn 5.9.0, macOS 27.0.1 Arm64. The fixture contains 1,000 commands, 1,001 events including
`ProcessorEvent`, 10 feature assemblies, 10 JSON contexts, 100 HTTP mappings, and 10,000 unrelated
types. The unchanged case generated 2,104 files totaling 16,035,608 UTF-8 bytes. The six source-emitting
Portia generators contributed 44 files totaling 3,142,978 bytes. Its source hash is
`779EC337C1CDC79C162EA380217CCF2BA4903C466F5220BA9CD15456465021AE`; the combined generated-source
hash is `46E8BDB4DD6C726716F1CE1D7237FAFB44C02A5BBA9EF031E3213D7B94D695B4`; and the harness source
hash is `54bafeb6ca9f74462a47a86026eb865791bf99bb2077c242c25b7ff1c23aab31`. The remaining 2,060
files and 12,892,630 bytes came from the .NET `System.Text.Json` generator and are excluded from the
Portia review below.

The existing output-identity campaign compiled the generated consumer for all eight input variants
and verified event-registration identity, JSON-root identity, and expected invalidation. The review
below examines the unchanged fixture. File sizes, line counts, and sample hashes are read from
`current/manifest.json` and `generated-source-content/<sha256>.g.cs` produced by that campaign.

| Portia generator | Files / total bytes | Largest Portia output | Reviewability and flow |
| --- | ---: | --- | --- |
| `DomainEventCatalogGenerator` | 18 / 91,436 | 6,333 B, 81 lines (`PortiaEventCatalog.6.g.cs`, `4B2431F661CCA15C1B5FEADAA5ED30D9D02F09E60FE91702E5E0862244DEBCA0`) | Strongest result: the shared entry point forwards into 17 bounded buckets; each leaf is a short sequence of typed `catalog.Register<T>` calls. |
| `JsonMetadataDiagnosticGenerator` | 0 / 0 | No output in the valid fixture | Diagnostic-only generator; generated-source ergonomics do not apply. |
| `JsonRootMarkerGenerator` | 1 / 1,347,660 | 1,347,660 B, 2,043 lines (`PortiaJsonRoots.g.cs`, `700093276474CA3725768AAE96DBEB77650A321E40FA09C3B4FA09E93CE2F724`) | Concern: one compilation-wide file holds all root attributes and context factories. Long assembly attributes reach 830 characters because the factory identity is encoded into the generated type name. The one-file output is difficult to inspect at this fixture scale. |
| `PortiaServiceRegistrationGenerator` | 1 / 928 | 928 B, 12 lines (`PortiaGeneratedServiceCollectionExtensions.g.cs`, `D6A29080D974BF5DDF1EE3CD35AF396A512DC7EE6B12649AEB21526699A195C3`) | The generated `AddCommand0` flow is direct and documented. Its registration initializer is a single 502-character line, which is harder to scan. |
| `ProjectorReactorEventDispatcherGenerator` | 2 / 2,132 | 1,068 B, 17 lines (`BenchmarkProjector_…EventDispatcher.g.cs`, `DCF498F444936431CF11380E39C58978ECF31092EB24AA21D2D2A230E92133D2`) | Clear typed switch, handler call, context construction, and default result path. The reactor output has the same shape. |
| `RegistrationCallInterceptorGenerator` | 20 / 840,014 | 727,855 B, 8,054 lines (`PortiaRegistrationInterceptors.CompilerFixturesComposition.ef449c25da726addf3fd2dc71fa979b92b5667c76f6f2b66472c62bc51676d09.g.cs`, `09CE2B65A67087CE04A8E7C559E1FA82F2C1B9B2E2AF9F0C78B4A27883980CCE`) | Shared registration blocks have separate helpers, so the compilation-wide event catalog is not repeated at every call site. The composition interceptor output places 1,006 intercepted call sites in one very large file. |
| `RequestHttpBindingGenerator` | 2 / 860,808 | 811,220 B, 13,207 lines (`PortiaHttpBinding.Root.g.cs`, `DE1FD6E9A9CC61904CA3F263F8A34971962DA820A836C062843C582C8353ED33`) | Request parsing, bus dispatch, and HTTP result mapping are visible in the generated flow. The 100-mapping fixture concentrates the bindings in one large root file rather than bounded shards. |
| `RequestShapeDiagnosticsGenerator` | 0 / 0 | No output in the valid fixture | Diagnostic-only generator; generated-source ergonomics do not apply. |

The generator driver also ran the .NET JSON source generator; its 2,060 generated files are not
Portia-owned and were excluded from the table. The two diagnostic-only Portia generators were
invoked by the shared fixture, but this report does not judge their diagnostic wording or locations.

## Real application cross-check

I also built `examples/Portia.Accounts/Portia.Accounts.Api` in Release mode from the same worktree.
With `PortiaSmokePackageVersion` unset, the API and Application projects reference the in-tree
Portia projects and generators. Each project's default generated-source directory was retained
under its own `obj/Release/net11.0/generated` folder to avoid collisions between equal hint names.
The build passed with zero warnings and errors.

Across the API and Application projects, six source-emitting Portia generators produced 15 files,
63,213 bytes, and 832 lines: 3 `Portia.Generators` files / 2,820 bytes / 52 lines in the API, 2
`Portia.AspNetCore.Generators` files / 38,311 bytes / 546 lines in the API, and 10
`Portia.Generators` files / 22,082 bytes / 234 lines in the Application project.

| Portia generator | Files / bytes / lines | Inspection |
| --- | ---: | --- |
| `DomainEventCatalogGenerator` | 2 / 1,076 / 24 | A short extension and shared helper register the two account events with typed calls. |
| `JsonRootMarkerGenerator` | 1 / 5,398 / 10 | Six assembly root attributes and one context factory fit in one file. The longest attribute line is 904 characters because the factory identity is encoded into the generated type name (`PortiaJsonRoots.g.cs`, SHA-256 `ED3BC0628E5B362CA8C04667D924BFF519004E5977CB3485E88269F64430A3A3`). |
| `PortiaServiceRegistrationGenerator` | 1 / 2,638 / 24 | Three documented `Add…` methods null-check the builder and register their request transport. The initializers are long single lines. |
| `ProjectorReactorEventDispatcherGenerator` | 1 / 1,512 / 19 | The account projector has an explicit typed switch for `AccountOpened` and `FundsDeposited`, handler calls, and default result path. |
| `RegistrationCallInterceptorGenerator` | 8 / 14,278 / 209 | The largest file is 8,220 bytes / 87 lines. Feature, application, and API wrappers call shared registration helpers; event and JSON-context registration bodies live in separate helper files. |
| `RequestHttpBindingGenerator` | 2 / 38,311 / 546 | The root is 36,242 bytes / 517 lines (`PortiaHttpBinding.Root.g.cs`, SHA-256 `A425D36898435E95B67F51EF30F0002A2DA43CE82F02FEA47C5F7BBAA1D99EEA`). It contains three endpoint maps with visible request binding, bus dispatch, and HTTP result mapping. |

The two diagnostic-only Portia generators emit no source in this valid example. The .NET JSON and
ASP.NET Core generators also emitted files; they are not included in Portia's 63,213-byte total.
The real example shows that the stress fixture's very large single files are not typical of this
small application. The 904-character JSON-root line and long registration initializers do occur in
the real sample.

Reproduce this build with:

```sh
dotnet build examples/Portia.Accounts/Portia.Accounts.Api/Portia.Accounts.Api.csproj --configuration Release -p:EmitCompilerGeneratedFiles=true
```

## Assessment

The event catalog and dispatcher outputs have readable control flow; event catalog output also has
bounded, shared structure. The small service-registration output is understandable, though its long
initializer line could be wrapped. At the combined workload size, the JSON-root, registration
interceptor, and HTTP-binding generators each produce a single source file larger than 700 KB.
Those files compile, but their size and long lines make interactive inspection and review difficult.
The large files contain 2,030 JSON-root attributes, 1,006 intercepted call sites, and 100 endpoint
mappings, respectively. This one workload does not measure how output size changes at smaller or
larger counts. Event-registration bucket bodies are separate from the interceptor wrappers, which
call shared helper methods instead of embedding those bodies at each intercepted call site.

`docs/project-structure.md` defines the reviewability and sharing goals but no maximum file size or
line length. These measurements identify ergonomic pressure, not a threshold failure. The Accounts example
produces moderate output files, while the combined stress fixture surfaces large-file behavior and
the long JSON-root line occurs in both. The generated consumer compilation is evidence of correctness
for these workloads, not proof of usability. A follow-up can choose a maximum line length or file
size, then determine whether to shard the stress outputs or shorten the encoded context identity.

## Reproduction

From a clean checkout with the .NET 11 SDK, run the existing output-capture campaign:

```sh
bash scripts/compiler-output-identity-campaign.sh /tmp/portia-generated-code-ergonomics
```

Inspect `current/manifest.json` for the `unchanged` case. Each `outputs` entry maps a generator and
hint name to a content hash; the corresponding source is in
`generated-source-content/<hash>.g.cs`. The campaign checks the generated consumer compilation and
all eight edit cases before preserving the raw output archive.

To capture the real example, run `dotnet build examples/Portia.Accounts/Portia.Accounts.Api/Portia.Accounts.Api.csproj --configuration Release -p:EmitCompilerGeneratedFiles=true`. Outputs are written beneath each project's own `obj/Release/net11.0/generated` directory.

The combined fixture capture records one macOS Arm64 run at the commit above; the Accounts build
is a local Release build from that same source. This report does not qualify runtime performance,
NativeAOT, hosted CI, or consumer usability.
