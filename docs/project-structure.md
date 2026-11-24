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
| `{App}.Core` | Aggregates, handlers, reactors, projectors, repositories, and their feature registration. Its application project dependencies are Common and any `{App}.*.Abstractions` projects. |
| `{App}.Api` | HTTP routes, transport mapping, host composition, transport DTOs, serializers, and provider adapters. |
| `{App}.Worker` | Background hosting, host composition, transport DTOs, serializers, and provider adapters, calling the same Core operations. |
| `{App}.Mcp` | MCP host composition and transport mapping, with no authored aggregates, contracts, or business components. |
| `{App}.Shared` | Shared non-business library code used by multiple hosts or layers. |
| `{App}.Host` | Explicit host composition outside the Api/Worker/Mcp naming convention. |

Api, Worker, Mcp, and Host are composition boundaries, not business-model boundaries. Keep aggregates, request and
event contracts, and recognized Portia business components in Common or Core. Host projects may
reference Common and Core, but must not reference sibling hosts. Derive the application identity from
the project name (`Shop.Api`) or role-qualified assembly name (`Shop.Api`) and check sibling
references in both directions.

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
    Serialization/

Core/Features/{Feature}/
    Aggregates/
        Entities/
    Handlers/
    Reactors/
    Projectors/
    Repositories/
    Authorization/
    Ports/
    Values/
    {Feature}PortiaExtensions.cs

Core/Execution/
    # shared request guards and pipeline behaviors
```

Use the established nested role folders shown above and shared Core areas described below. Keep
feature folders flat within each role and use the same feature names in Common and Core. If a concept
fits nowhere, reconsider the concept or amend the rule explicitly. There is one registration file
per Core feature, rather than one per component.

An entity is an aggregate-owned child with its own stable identity and lifecycle. Use an ordinary
class (a record class is also allowed) and let the aggregate's declared state establish ownership:
exactly one concrete root in the same feature stores it in an instance field or auto-property,
directly or through a collection or array. The root remains the command entry point and enforces
rules spanning its children. Place entities in `Core/Features/{Feature}/Aggregates/Entities/`;
keep aggregate roots in `Aggregates/`.
Orphan entities, entities stored by multiple roots, and roots placed in `Entities/` are structural
errors. Identity representation belongs to the application; an entity need not expose a uniform
`Id` member.

Declare one top-level type per file, including enums and delegates. Nested types stay with their owner. Match the filename to the type; generic filenames use `OfT` for one parameter and `OfT2` for two, for example `Page<T>` in `PageOfT.cs`. A type already ending in the appropriate suffix does not repeat it. Partial declarations may use an aspect name such as `PageOfT.Serialization.cs`. Namespaces are the project's
`RootNamespace` followed by its folders, including in test projects. Api and Worker can use the
ordinary top-level `Program.cs` composition root.

| Core folder | Responsibility |
| --- | --- |
| Aggregates | Aggregate roots and directly owned rule or state collaborators. Keep local invariants visible and prefer composition as an aggregate grows beyond the configured semantic responsibility budget (12 by default). Child entities with their own identity and lifecycle live in `Aggregates/Entities/`. |
| Handlers | One handler per command or query; keep unrelated types elsewhere. |
| Reactors | Map committed facts to commands and send them. Do not hydrate aggregates or re-verify the source fact. |
| Projectors | Write read models through repositories. |
| Repositories | Own the feature's projection writes and queries; reads follow handler → repository → store. Queries never hydrate aggregates. |
| Authorization | Authorizers that read state or apply a real rule. A check every command shares belongs in registration rather than another file. |
| Ports | Interfaces to dependencies outside the feature, with a production implementation and a test that need them. |
| Values | Feature-owned values that are part of Core behavior. |
| Execution | Shared guards and pipeline behaviors used across features. Feature-specific components stay in their feature role folders. |

Start aggregate sizing decisions with the invariants and their owners. Extract an entity when it has
its own identity and lifecycle and participates in an atomic rule with its root. Entity methods may
propose facts about the entity's own lifecycle; the root decides whether those facts fit
aggregate-wide invariants and emits them on the root stream. Replay handlers rebuild both root and
child state from those facts. `PORTIA208` counts declared public command methods and distinct `On<TEvent>` replay/event
responsibilities rather than physical lines, so formatting and comments do not change the result.
Its default review budget is 12 responsibilities; set
`portia_aggregate_semantic_budget` in `.editorconfig` for a stricter or looser boundary. Treat the
threshold as a prompt to inspect these boundaries rather than as a target.

Composition is the way to keep a large aggregate reviewable. Aggregate collaborators must be directly referenced by an aggregate in the feature. Models have data-only shapes, values have immutable value shapes, and repository implementations satisfy a persistence contract or an interface declared in the feature. Concept budgets and helper-chain quality remain review responsibilities.

Static rules helpers and error factories belong inside the type that owns their business rule:
use private methods or nested types in the aggregate for aggregate invariants, or in the handler
for operation-specific orchestration. A separate aggregate collaborator is appropriate when the
aggregate directly uses it. Core enums follow that ownership too; reusable contract enums belong
in the Common feature's `Models/` folder. Common `Values/` holds immutable record values, not enums.
There is no general-purpose static-helper or global error-catalog role folder.

Prefer one `[PortiaJsonContext]` per serializer-owning assembly. In Common/Core, an assembly-wide
context belongs in a file at the project root with the project's root namespace, for example
`Shop.Common/ContractsJsonContext.cs`. This slot requires the existing marker and a valid
`JsonSerializerContext` shape; it is not a general exception for root-level helpers. When contracts
are serialized per feature, put that feature's context in its `Serialization/` folder, for example
`Shop.Common/Features/Orders/Serialization/OrdersJsonContext.cs`. `Serialization/` holds only
`[PortiaJsonContext]` contexts. Api/Worker may organize their context alongside other serialization
composition. Additional contexts can support genuinely separate serializer configurations; context
count is a review choice.

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

Set a role explicitly in `.editorconfig`:

```ini
[src/Shop.Mcp/**.cs]
portia_project_role = Mcp

[src/Shared.Hosting/**.cs]
portia_project_role = Host

[src/Shop.Core/**.cs]
portia_aggregate_semantic_budget = 10
```

`PortiaProjectRole` in `Directory.Build.props` is a build-time override:

```xml
<PropertyGroup>
  <PortiaProjectRole>Shared</PortiaProjectRole>
</PropertyGroup>
```

The application diagnostics below are implemented in the current source checkout for the next
`Cntryl.Portia.Analyzers` release. Reference that package in application and test projects, and
enable the convention once in the application's shared `Directory.Build.props`:

```xml
<PropertyGroup>
  <PortiaProjectStructure>true</PortiaProjectStructure>
</PropertyGroup>
```

The package exports the project directory, project name, root namespace, and `IsTestProject` to
Roslyn. Application project names end in `.Common`, `.Core`, `.Api`, `.Worker`, `.Mcp`, `.Host`, or `.Shared`.
Use `portia_project_role` or `PortiaProjectRole` for nonstandard names. Test projects
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
| PORTIA204 | A known component role is in the wrong folder, or a port is not an interface. It also rejects authored aggregates, request/event contracts, and recognized Portia business components declared in Api or Worker; `Aggregates/Entities/` holds plain child classes, each owned by one same-feature aggregate root. | Diagnostic identifies the folder's responsibility or missing/ambiguous entity owner. |
| PORTIA205 | One handler implements contracts for multiple requests. | Separate the operations deliberately. |
| PORTIA206 | Common references another application project, or Core references one other than Common or a `*.Abstractions` project; either references a known infrastructure adapter; Cassie connection setup occurs outside Api/Worker; Api and Worker reference each other. | Move composition to a root; Core's Cassie repository API is allowed. |
| PORTIA207 | Core component registration occurs outside its feature registration file. | Diagnostic names the destination file. |
| PORTIA208 | An aggregate exceeds the semantic responsibility budget (12 by default). | Formatting and comments do not affect the count. Start with each command/event lifecycle and its owner; extract a child entity when it has its own identity and lifecycle within an atomic root rule. |
| PORTIA209 | Common declares executable members, except construction of sealed record values in Values. | Move other behavior into Core. |
| PORTIA210 | A request/event lacks a valid feature discriminator or explicit positive version. | Make an omitted default version explicit; never guess or change a wire identity. |
| PORTIA211 | A command handler's implementation consists solely of throwing `NotImplementedException`. | Implement the operation or remove its surface. |
| PORTIA212 | A query handler or reactor reaches Portia aggregate hydration/execution APIs through statically bound calls in the same compilation. | Query a repository, or send an authoritative command from the reactor. Advisory reads may reject early, but only the participant's authoritative command outcome confirms or rejects the business assumption. |
| PORTIA213 | A production application project has an unsupported role. | Use Common, Core, Api, Worker, Mcp, Host, or Shared. Set `portia_project_role` in `.editorconfig` or `PortiaProjectRole` in MSBuild. |

Constructors for sealed Common value records may validate, normalize, and initialize state. An analyzer cannot prove that arbitrary constructor callees are pure; known infrastructure dependencies remain subject to dependency diagnostics.

That construction exemption is limited to `Values/`. Executable constructors in `Commands/` or
`Models/` remain subject to PORTIA209. For contract input requiring normalization or validation,
represent the validated datum as a sealed record value in `Values/` and compose it into the command
or model, or perform operation-specific validation in Core. A total warning count alone does not
establish which construction patterns were reported.

Code actions name the exact change and use the IDE's normal preview. File actions decline existing
destination files or directories and linked files. Splitting moves nested types with their owner and declines
file-local types and conditional directives that require a semantic decision. File-leading
`#nullable` and `#pragma warning` directives are copied to each split file. Namespace changes use
[Roslyn's document namespace refactoring](https://source.dot.net/Microsoft.CodeAnalysis.Features/src/roslyn/src/Analyzers/Core/CodeFixes/MatchFolderAndNamespace/AbstractChangeNamespaceToMatchFolderCodeFixProvider.cs.html)
to update references, and decline known destination conflicts and partial types spread across files.
Fix All supports document, project, and solution scopes for these actions. File operations check
for collisions among solution documents and existing files or directories on disk.
In the tested .NET 10 MSBuild workspace, adding documents also adds explicit `Compile` items that
conflict with SDK default globs, while replacement renames did not take effect. PORTIA200 and
PORTIA201 file fixes are therefore unavailable in that workspace; PORTIA202 can still be applied by
`dotnet format`. The Portia source checkout includes a separate CLI for the file operations:

```sh
dotnet restore App.slnx
dotnet run --project /path/to/portia/eng/Portia.RepositoryTools/Portia.RepositoryTools.csproj \
  -- fix-application-structure --solution App.slnx
```

The command applies file writes and moves itself, then reloads the solution and checks that each
destination is included exactly once. It does not add explicit `Compile` items. If an explicit
Compile rule excludes a destination, a collision exists, the reloaded solution differs, or the build
fails, the CLI restores the original files and reports the failure. It builds with `--no-restore`,
checks that PORTIA200/201 are clear, and repeats the diagnostic pass to confirm idempotence. Projects
with custom Compile rules must be updated manually before rerunning the command. The normal Roslyn
code actions remain available in hosts that support safe file operations.

Analyzer tests compile small application samples and exercise the diagnostics and code actions.
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
