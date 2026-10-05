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
dotnet restore eng/Portia.RepositoryTools --locked-mode
dotnet build eng/Portia.RepositoryTools --configuration Release --no-restore
mkdir -p "$portia_compiler_output"
dotnet msbuild bench/Portia.CompilerBenchmarks/Portia.CompilerBenchmarks.csproj \
  -target:ResolveReferences -getItem:Analyzer -verbosity:quiet > "$portia_compiler_output/resolved-analyzers.json"
portia_json_generator=$(dotnet run --project eng/Portia.RepositoryTools --configuration Release --no-build -- \
  resolve-json-generator "$portia_compiler_output/resolved-analyzers.json")
git rev-parse HEAD > "$portia_compiler_output/source-commit.txt"
git status --porcelain > "$portia_compiler_output/source-status.txt"
if [[ -s "$portia_compiler_output/source-status.txt" ]]; then
  echo "Compiler campaigns require committed, clean source." >&2
  exit 1
fi
dotnet --info > "$portia_compiler_output/dotnet-info.txt"
for portia_profile in plain small medium large features json http unrelated-1000 unrelated-10000 combined; do
  if [[ -n "$portia_compiler_filter" && "$portia_profile" != *"$portia_compiler_filter"* ]]; then
    continue
  fi
  # Bound memory retention and isolate each workload's compiler/JIT lifetime. Each process still
  # performs its full output controls and warmup before collecting the declared repetitions.
  portia_profile_output="$portia_compiler_output/profiles/$portia_profile"
  dotnet run --project bench/Portia.CompilerBenchmarks --configuration Release --no-build \
    -- "$portia_profile_output" "$portia_json_generator" "$portia_compiler_repetitions" "$portia_profile"
  dotnet run --project eng/Portia.RepositoryTools --configuration Release --no-build -- \
    compiler-build-campaign "$portia_profile_output" "$portia_build_repetitions"
done
dotnet run --project eng/Portia.RepositoryTools --configuration Release --no-build -- \
  compiler-merge-campaign "$portia_compiler_output" "$portia_compiler_repetitions"
