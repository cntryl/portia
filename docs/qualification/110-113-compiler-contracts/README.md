# Compiler contract qualification (#110–#113)

Compiler baseline: `e734be327106b710264f26270f101bdfa059e016`; the batch incorporates the independent allocation-test isolation repair from `1241b689258b753c87d0d453b261c4b9b3d52622`. Neither commit changes compiler behavior relative to the other.

## Regressions and controls

`red-regressions.txt` lists 15 failures established against unchanged compiler code: six discarded-assertion cases, three context-identity collisions, one wrong-base context, and five valid HTTP method-group/expression-tree consumers. The HTTP baseline replay uses the unchanged generator with the corrected, complete consumer imports and checks output compiler diagnostics. An earlier broader unsupported-context probe emitted inaccessible generated references and aborted inside Roslyn; the corrected implementation completes all those cases without generating invalid context references.

- **#110:** source-located `PORTIA107` covers discarded assignments and expression-bodied void callbacks for commands, queries and streams, including augmentation whose original expectations are later awaited. Retained callbacks/values, explicit `AsTask()`, pragma suppression, and a real local named `_` remain valid. Runtime controls prove the discarded assertion does not execute, the retained original executes without that assertion, and the corrected assertion fails with one execution even after repeated awaits. Immutable helper semantics remain unchanged.
- **#111:** factory identities encode a length-delimited full assembly identity and fully qualified context identity as UTF-16 hex. Namespace/nesting/underscore/escaped-identifier collisions compile; declaration order produces identical source. Two separately compiled assemblies whose names previously sanitized identically compose without factory ambiguity. Existing arbitrary/legacy factory metadata remains authoritative. Applications do not need to reference generated class names.
- **#112:** shared validation filters unusable contexts from factory emission, registration, coverage and code-fix selection. `PORTIA030` covers visibility through enclosing types, file-local/generic/abstract/wrong-base shapes, unusable options constructors, ambiguous overloads, required members, and partial contexts without roots. Optional options-constructor arguments remain valid. Code fixes can add a first root to a usable empty partial context. Suppressing the diagnostic does not re-enable invalid emission; partial and referenced contexts retain the positive controls.
- **#113:** semantic HTTP call-site inspection diagnoses method groups, configured overloads and expression trees. Ordinary mappings, named arguments and static-import extension invocation remain supported; bare unqualified extension-method groups receive C#'s own source error. The fallback and XML guidance identify `Cntryl.Portia.AspNetCore` as the HTTP generator owner. Unrelated method names remain outside the semantic match.

## Actual packages and NativeAOT

```sh
dotnet pack Portia.slnx --configuration Release --no-build --no-restore --output artifacts/packages -p:PackageVersion=0.7.0-ci.110.113
python3 eng/verify-packed-compiler.py --version 0.7.0-ci.110.113 --packages artifacts/packages
```

The script uses a new external directory/cache, explicit independent analyzer assets, application-source diagnostics, and no project references. It requires successful direct HTTP compilation with and without the independent general analyzer package; it rejects false success or an incidental build failure when a specific diagnostic is required. Six packed request assertions verify `PORTIA107`; packed contexts verify `PORTIA030`; HTTP-only and general-analyzer consumers verify `PORTIA016`. A packed executable with the HTTP analyzer item explicitly removed before compilation invokes the real fallback and requires its message to name the ASP.NET Core owner.

The HTTP NativeAOT consumer references the separate `Portia.Json.Contracts` assembly containing `App_A.Context` and `App.A_Context`. Their internal serializer contexts are composed through exported factory metadata. The executable checks reflection-disabled JSON and serializes/deserializes both contracts using Portia's configured generated metadata. Managed fresh-cache execution passes; CI restores the same packages in another fresh cache, publishes Linux NativeAOT and executes it. Existing HTTP, MCP, storage and broker gates remain required on both final PR and merged commits.

## Review

Adversarial review checked identity injectivity and ordering, metadata compatibility, constructor/required-member failures before emission, code-fix prospective first-root validity, diagnostic suppression, semantic discard detection versus a local `_`, duplicate diagnostic avoidance, explicit execution and void-callback semantics, source errors versus generator diagnostics, and HTTP generator ownership. No public signatures or external dependencies change, and no performance improvement is claimed.
