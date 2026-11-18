# VS Code replay

The adapter uses the locally installed VS Code, C# extension and C# Dev Kit. It does not download
an editor or change settings in the user's normal profile. Its temporary profile disables extension
updates and unrelated user extensions; logs record exactly which extensions and analysis settings ran.

Build the compiler harness and export a new disposable workspace:

```sh
dotnet build bench/Portia.CompilerBenchmarks --configuration Release
dotnet bench/Portia.CompilerBenchmarks/bin/Release/net11.0/Portia.CompilerBenchmarks.dll editor-fixture \
  --output artifacts/compiler/editor-workspace
dotnet restore artifacts/compiler/editor-workspace/Bench.Editor.csproj
node --test bench/Portia.CompilerBenchmarks/vscode/state.test.cjs
```

Run the two generator execution modes separately:

```sh
node bench/Portia.CompilerBenchmarks/vscode/launch.cjs artifacts/compiler/editor-workspace Automatic
node bench/Portia.CompilerBenchmarks/vscode/launch.cjs artifacts/compiler/editor-workspace Balanced
```

`Automatic` measures an edit followed by a fresh diagnostic while typing. `Balanced` measures an
edit and save followed by a fresh diagnostic. The installed C# extension defaults to `Balanced`;
expecting source-generator diagnostics after each unsaved edit in that mode would incorrectly
report a timeout. The selected mode lives only in the temporary profile and appears in every report.

The launcher targets macOS's normal VS Code app path. `VSCODE_EXECUTABLE` can select another local
installation. It uses a short temporary profile path for macOS socket limits, enforces a three-minute
run deadline, and retains the launch log and temporary profile location in `artifacts/compiler`.
The existing licensed editor session and account settings stay in their original profile.

The replay measures processor diagnostics, contract metadata diagnostics, compiler syntax feedback,
code-fix discovery/resolution, and application of the processor partial fix. A unique diagnostic
marker acknowledges each request, so old diagnostics cannot count as new feedback. Source files
are restored before a successful completion record is written. Completed samples and failures remain
in `editor-*.jsonl` under the disposable workspace.

The first processor request includes project/server initialization. It is labeled separately from
subsequent edits. Three observations are a pilot, not a P95 qualification campaign. Each feedback
measurement acknowledges its named diagnostic; VS Code's public diagnostics API does not expose
completion of all C# analysis, so `completeAnalysisMilliseconds` remains null. Compiler feedback
for an unrelated body edit cannot establish when every generator/reactor diagnostic finished.

The adapter uses the
documented [VS Code diagnostics API](https://code.visualstudio.com/api/references/vscode-api#languages)
and [code-action provider command](https://code.visualstudio.com/api/references/commands#commands).
