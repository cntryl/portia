# Application project structure

A reviewer should be able to tell what a file owns from its folder and name, and review an
operation without following a chain of helpers. The same predictable layout lets an agent find
the right place for a change without inventing another convention. Keep rules explicit and the
resulting code easy for people to reason about.

These are application conventions. Portia's framework packages have their own public namespace
and package contracts. Enforce mechanically decidable application rules with analyzers, and
review business meaning and ownership. Do not add repository structure tests or weaken existing
diagnostics to accommodate a violation.

## Projects and dependencies

| Project | Owns |
| --- | --- |
| `{App}.Common` | Commands, queries, events, models, and shared values grouped by feature; data contracts without executable behavior. |
| `{App}.Core` | Aggregates, handlers, reactors, projectors, repositories, and their feature registration. Its application project dependency is Common. |
| `{App}.Api` | HTTP routes, transport mapping, and application composition. |
| `{App}.Worker` | Background hosting and application composition, calling the same Core operations. |

Portia owns the Cassie store integration. An application does not introduce a Persistence
project. Core repositories may derive from `CassieProjectionRepository` and use its query and
transaction API. Api and Worker configure the shared connection through `AddCassie`; Core feature
registration may use `AddCassieProjection`. Keep other concrete infrastructure configuration in
the composition roots.

Test projects mirror source projects one to one, with test folders mirroring the source folders.
Use `Should<Outcome>Given<State>[When<Action>]` names and Arrange, Act, Assert phases.

## Features and files

```text
Common/Features/{Feature}/
    Commands/
    Queries/
    Events/
    Models/
    Values/

Core/Features/{Feature}/
    Aggregates/
    Handlers/
    Reactors/
    Projectors/
    Repositories/
    Authorization/
    Ports/
    Values/
    {Feature}PortiaExtensions.cs
```

The folder list is closed. Keep these folders flat and use the same feature names in Common and
Core. If a concept fits nowhere, reconsider the concept or amend the rule explicitly. There is
one registration file per Core feature, rather than one per component.

Declare one top-level type per file, including enums and delegates. Nested types stay with their owner. Match the filename to the type; generic filenames use `OfT` for one parameter and `OfT2` for two, for example `Page<T>` in `PageOfT.cs`. A type already ending in the appropriate suffix does not repeat it. Partial declarations may use an aspect name such as `PageOfT.Serialization.cs`. Namespaces are the project's
`RootNamespace` followed by its folders, including in test projects. Api and Worker can use the
ordinary top-level `Program.cs` composition root.

| Core folder | Responsibility |
| --- | --- |
| Aggregates | Aggregate roots and their directly owned rule or state collaborators. Keep local invariants visible and prefer clean composition as an aggregate grows beyond roughly 400 lines. |
| Handlers | One handler per command or query; keep unrelated types elsewhere. |
| Reactors | Map committed facts to commands and send them. Do not hydrate aggregates or re-verify the source fact. |
| Projectors | Write read models through repositories. |
| Repositories | Own the feature's projection writes and queries; reads follow handler → repository → store. Queries never hydrate aggregates. |
| Authorization | Authorizers that read state or apply a real rule. A check every command shares belongs in registration rather than another file. |
| Ports | Interfaces to dependencies outside the feature, with a production implementation and a test that need them. |
| Values | Feature-owned values that are part of Core behavior. |

Composition is the way to keep a large aggregate reviewable. Aggregate collaborators must be directly referenced by an aggregate in the feature. Models have data-only shapes, values have immutable value shapes, and repository implementations satisfy a persistence contract or an interface declared in the feature. Concept budgets and helper-chain quality remain review responsibilities.

Static rules helpers and error factories belong inside the type that owns their business rule:
use private methods or nested types in the aggregate for aggregate invariants, or in the handler
for operation-specific orchestration. A separate aggregate collaborator is appropriate when the
aggregate directly uses it. Core enums follow that ownership too; reusable contract enums belong
in the Common feature's `Models/` folder. Common `Values/` holds immutable record values, not enums.
There is no general-purpose static-helper or global error-catalog role folder.

Prefer one `[PortiaJsonContext]` per serializer-owning assembly. In Common/Core, an assembly-wide
context belongs in a file at the project root with the project's root namespace, for example
`Platform.Common/ContractsJsonContext.cs`. This slot requires the existing marker and a valid
`JsonSerializerContext` shape; it is not a general exception for root-level helpers. Api/Worker
may organize their context alongside other serialization composition. Additional contexts can
support genuinely separate serializer configurations; context count is a review choice.

A repository does not always need a feature-local interface: an implementation that satisfies an
existing persistence contract, such as `IProjectionStore`, `IProjectionCheckpointStore`, or
`CassieProjectionRepository`, is accepted directly. For an
application-specific repository, a feature-local interface describes the feature's read/write
boundary. Registering and calling a concrete helper alone does not establish that it is a repository;
do not add an empty interface merely to silence PORTIA204.

## Contracts and business behavior

Commands name business actions and queries name requested information. Avoid module prefixes,
plural resource names, and numeric suffixes. Events name completed business facts in PascalCase.
Requests and events declare two snake_case discriminator parts, `{feature}.{name}`, and an
explicit positive schema version:

```csharp
[Discriminator("order_lines.place_order", 1)]
public sealed record PlaceOrder(Uuid OrderId) : IRequest;
```

Commands return no content, a receipt, or commit facts and values produced directly by the write.
They do not return read models. Events carry business facts, leaving metadata and stream identity
to Portia. Remove superseded contracts, routes, projectors, and tests together before release;
do not introduce compatibility shims for an unreleased design.

Local aggregate validation is authoritative within that aggregate's atomic consistency boundary.
Cross-aggregate reads can reject predictable failures early, but remain advisory. Choreography
must consume the owner's authoritative acceptance or rejection facts before completing an
operation, with explicit rejection, compensation, or intervention when an assumption fails.
Pending and unknown outcomes remain visible until resolved. See [validation authority](validation-authority.md)
for the race, replay, idempotency, and failure-resolution requirements.

Introduce a concept only when a production implementation and a test need it. Keep a command
reviewable in a handful of files; explain why more than roughly six are necessary. Do not ship
command handlers that only throw `NotImplementedException`. Any deliberate query API stubs must
be identified in one application-owned place.

Documentation lives under `docs/`. Keep one living document per aggregate describing its
invariants, identity, and concurrency decisions in prose. Remove raw AI review output, judgment
JSON, superseded designs, and other generated residue; Git retains history.

## Analyzer setup

The application diagnostics below are implemented in the current source checkout for the next
`Cntryl.Portia.Analyzers` release. Reference that package in application and test projects, and
enable the convention once in the application's shared `Directory.Build.props`:

```xml
<PropertyGroup>
  <PortiaProjectStructure>true</PortiaProjectStructure>
</PropertyGroup>
```

The package exports the project directory, project name, root namespace, and `IsTestProject` to
Roslyn. Application project names end in `.Common`, `.Core`, `.Api`, or `.Worker`. Test projects
set `IsTestProject=true`; filename and namespace checks still apply to them, while application
role, dependency, and business-component checks do not. Generated source is excluded. Folder
checks skip linked source outside the project because Roslyn does not expose its logical Link
path; they do not scan the filesystem or invent a folder.

The diagnostics are enabled warnings in the `PortiaStructure` category. To stage adoption using [Roslyn severity configuration](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/configuration-options):

```editorconfig
dotnet_analyzer_diagnostic.category-PortiaStructure.severity = none
dotnet_diagnostic.PORTIA211.severity = warning
dotnet_diagnostic.PORTIA212.severity = warning
```

The diagnostics are enabled warnings by default. `TreatWarningsAsErrors` makes them build failures. Normal
Roslyn severity configuration and deliberate suppressions apply. Existing Portia diagnostics
retain their behavior regardless of this opt-in.

| Diagnostic | Checks | Code fix |
| --- | --- | --- |
| PORTIA200 | Multiple declared types in a file. | Move a top-level type into its own named file. |
| PORTIA201 | A filename differs from its type's name, including `OfT` for generics. | Rename the file, retaining the type's name. |
| PORTIA202 | A namespace differs from RootNamespace plus folders. | Change the document namespace and update references across the solution. |
| PORTIA203 | Common/Core files fall outside the closed feature folders. | Diagnostic explains the allowed folders. |
| PORTIA204 | A known component role is in the wrong folder, or a port is not an interface. | Diagnostic identifies the folder's responsibility. |
| PORTIA205 | One handler implements contracts for multiple requests. | Separate the operations deliberately. |
| PORTIA206 | Common/Core reference other application layers or known infrastructure adapters; Cassie connection setup occurs outside Api/Worker. | Move composition to a root; Core's Cassie repository API is allowed. |
| PORTIA207 | Core component registration occurs outside its feature registration file. | Diagnostic names the destination file. |
| PORTIA208 | An aggregate file exceeds 400 lines. | Compose smaller rule/state objects; choose the boundary deliberately. |
| PORTIA209 | Common declares executable members, except construction of sealed record values in Values. | Move other behavior into Core. |
| PORTIA210 | A request/event lacks a valid feature discriminator or explicit positive version. | Make an omitted default version explicit; never guess or change a wire identity. |
| PORTIA211 | A command handler's implementation consists solely of throwing `NotImplementedException`. | Implement the operation or remove its surface. |
| PORTIA212 | A query handler or reactor directly calls Portia aggregate hydration/execution APIs. | Query a repository, or send an authoritative command from the reactor. |
| PORTIA213 | A production application project has an unsupported role. | Use Common, Core, Api, or Worker. |

Constructors for sealed Common value records may validate, normalize, and initialize state. An analyzer cannot prove that arbitrary constructor callees are pure; known infrastructure dependencies remain subject to dependency diagnostics.

That construction exemption is limited to `Values/`. Executable constructors in `Commands/` or
`Models/` remain subject to PORTIA209. For contract input requiring normalization or validation,
represent the validated datum as a sealed record value in `Values/` and compose it into the command
or model, or perform operation-specific validation in Core. A total warning count alone does not
establish which construction patterns were reported.

Code actions name the exact change and use the IDE's normal preview. File actions decline existing
destination files and linked files. Splitting moves nested types with their owner and declines
file-local types and conditional directives that require a semantic decision. File-leading
`#nullable` and `#pragma warning` directives are copied to each split file. Namespace changes use
[Roslyn's document namespace refactoring](https://source.dot.net/Microsoft.CodeAnalysis.Features/src/roslyn/src/Analyzers/Core/CodeFixes/MatchFolderAndNamespace/AbstractChangeNamespaceToMatchFolderCodeFixProvider.cs.html)
to update references, and decline known destination conflicts and partial types spread across files.
Fix All supports document, project, and solution scopes for these actions. File operations check
for collisions among solution documents and existing files on disk.
In the tested .NET 10 MSBuild workspace, adding documents also adds explicit `Compile` items that
conflict with SDK default globs, while replacement renames did not take effect. PORTIA200 and
PORTIA201 file fixes are therefore unavailable in that workspace; PORTIA202 can still be applied by
`dotnet format`. In a host that supports file operations, the adoption order is:

```sh
dotnet format App.slnx --diagnostics PORTIA202
# Reopen the solution.
dotnet format App.slnx --diagnostics PORTIA200
# Reopen the solution.
dotnet format App.slnx --diagnostics PORTIA201
```

Repeat the sequence to confirm that no further changes are needed. The current MSBuild workspace
cannot complete the PORTIA200/201 steps safely; resolve those IDs in a host that supports file
creation and document replacement until the workspace behavior is fixed.

Analyzer tests compile small application samples and exercise the diagnostics and code actions.
Run `bash scripts/verify-namespace-fix.sh /tmp/portia-namespace-check 64` after a Release build to
exercise PORTIA202 through the installed SDK's `dotnet format` host, with generated JSON constructor
references, both namespace styles, compilation, and a second pass for idempotence.
They are not repository structure tests. They do not establish business semantics, absence of
indirection, production implementations/tests for every port, or a complete cross-project graph.
Those remain review requirements, alongside authoritative outcome-validation behavior tests.
When a repeated violation is mechanically distinguishable, add an analyzer rather than another
convention test or an abstraction whose only purpose is to placate a check.

## Generated code

The same reviewability goal applies to the framework's generators. Generated source grows with
the number of components and contracts; it must not multiply a compilation-wide catalog by
the number of registration calls. Emit shared blocks once and initialize them once per builder
and producing assembly, with retry when initialization fails.

Design-time work filters syntax before binding symbols, keeps per-node models independent of
unrelated files, and compares models by value so unchanged output can be cached. Preserve
diagnostic locations, cancellation, invalid-code handling, and changes to referenced contracts.

Runtime dispatch uses typed calls and cached metadata. Remove avoidable repeated setup,
reflection, captured callbacks, and resizing buffers. Preserve handler order, failure propagation,
context ownership, and retained batch contents; allocation reduction never justifies reusing a
context or buffer that application code can still observe. Qualify changes with the compiler
campaign and the relevant runtime benchmarks, reporting source size, incremental generation,
startup, allocations, and dispatch costs separately. See [performance and scaling](performance-and-scaling.md).
