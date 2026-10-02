#!/usr/bin/env bash
set -euo pipefail
portia_compiler_root=$(cd "$(dirname "$0")/.." && pwd)
portia_compiler_output=${1:?Provide an evidence directory}
portia_compiler_repetitions=${2:-20}
portia_compiler_filter=${3:-}
portia_build_repetitions=${4:-5}
if [[ -e "$portia_compiler_output/source-commit.txt" ]]; then
  echo "Use a fresh evidence directory to preserve prior campaign results." >&2
  exit 1
fi
cd "$portia_compiler_root"
dotnet restore bench/Portia.CompilerBenchmarks --locked-mode
dotnet build bench/Portia.CompilerBenchmarks --configuration Release --no-restore
mkdir -p "$portia_compiler_output"
dotnet msbuild bench/Portia.CompilerBenchmarks/Portia.CompilerBenchmarks.csproj \
  -target:ResolveReferences -getItem:Analyzer -verbosity:quiet > "$portia_compiler_output/resolved-analyzers.json"
portia_json_generator=$(python3 - "$portia_compiler_output/resolved-analyzers.json" <<'PY'
import json, sys
items = json.load(open(sys.argv[1]))['Items']['Analyzer']
matches = [item['Identity'] for item in items if item['Identity'].endswith('/System.Text.Json.SourceGeneration.dll')]
assert len(matches) == 1, matches
print(matches[0])
PY
)
git rev-parse HEAD > "$portia_compiler_output/source-commit.txt"
git status --porcelain > "$portia_compiler_output/source-status.txt"
if [[ -s "$portia_compiler_output/source-status.txt" ]]; then
  echo "Compiler campaigns require committed, clean source." >&2
  exit 1
fi
dotnet --info > "$portia_compiler_output/dotnet-info.txt"
dotnet run --project bench/Portia.CompilerBenchmarks --configuration Release --no-build \
  -- "$portia_compiler_output" "$portia_json_generator" "$portia_compiler_repetitions" "$portia_compiler_filter"
python3 scripts/compiler-build-campaign.py "$portia_compiler_output" "$portia_build_repetitions"
