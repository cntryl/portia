#!/usr/bin/env bash
set -euo pipefail
portia_runtime_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
portia_runtime_output=${1:?Usage: runtime-async-comparison.sh OUTPUT}
portia_runtime_project="$portia_runtime_root/bench/Portia.Benchmarks/Portia.Benchmarks.csproj"

if [[ "$portia_runtime_output" != /* ]]; then
  echo "Provide an absolute evidence directory outside the repository." >&2
  exit 1
fi
case "$portia_runtime_output/" in
  "$portia_runtime_root/"*)
    echo "Keep campaign outputs outside the repository." >&2
    exit 1
    ;;
esac

if [[ -e "$portia_runtime_output/source-commit.txt" ]]; then
  echo "Use a fresh evidence directory to preserve prior campaign results." >&2
  exit 1
fi

cd "$portia_runtime_root"
portia_runtime_source_status=$(git status --porcelain)
if [[ -n "$portia_runtime_source_status" ]]; then
  printf '%s\n' "$portia_runtime_source_status" >&2
  echo "Runtime Async comparisons require committed, clean source." >&2
  exit 1
fi

mkdir -p "$portia_runtime_output"
git rev-parse HEAD > "$portia_runtime_output/source-commit.txt"
git status --porcelain > "$portia_runtime_output/source-status.txt"
dotnet --info > "$portia_runtime_output/dotnet-info.txt"
dotnet --list-runtimes > "$portia_runtime_output/dotnet-runtimes.txt"
uname -a > "$portia_runtime_output/uname.txt"
dotnet restore "$portia_runtime_project"

for portia_runtime_mode in off on; do
  portia_runtime_mode_output="$portia_runtime_output/$portia_runtime_mode"
  mkdir -p "$portia_runtime_mode_output"
  if [[ "$portia_runtime_mode" == off ]]; then
    portia_runtime_property=false
  else
    portia_runtime_property=true
  fi

  dotnet build "$portia_runtime_project" --configuration Release --no-restore --no-incremental \
    -p:UseRuntimeAsync="$portia_runtime_property" \
    -p:BaseOutputPath="$portia_runtime_mode_output/bin/" \
    --verbosity diagnostic > "$portia_runtime_mode_output/build.log" 2>&1

  portia_runtime_assembly="$portia_runtime_mode_output/bin/Release/net11.0/Portia.Benchmarks.dll"
  if [[ ! -f "$portia_runtime_assembly" ]]; then
    echo "Expected benchmark assembly was not produced: $portia_runtime_assembly" >&2
    exit 1
  fi

  if [[ "$portia_runtime_mode" == on ]]; then
    if ! rg -Fq "/features:runtime-async=on" "$portia_runtime_mode_output/build.log"; then
      echo "The on build did not pass runtime-async=on to Roslyn." >&2
      exit 1
    fi
    rg -F -o "/features:runtime-async=on" "$portia_runtime_mode_output/build.log" \
      | wc -l | tr -d ' ' > "$portia_runtime_mode_output/on-switch-invocations.txt"
  else
    if rg -Fq "/features:runtime-async=on" "$portia_runtime_mode_output/build.log"; then
      echo "The off build unexpectedly passed runtime-async=on to Roslyn." >&2
      exit 1
    fi
    printf '0\n' > "$portia_runtime_mode_output/on-switch-invocations.txt"
  fi

  dotnet "$portia_runtime_assembly" --runtime-qualification \
    > "$portia_runtime_mode_output/controls.log" 2>&1

  dotnet "$portia_runtime_assembly" \
    --inProcess --job short \
    --filter '*RequestDispatchBenchmarks*' '*HttpLifecycleBenchmarks.Json*' '*ProcessorDispatchBenchmarks*' \
    --outliers DontRemove --allStats --keepFiles \
    --exporters json \
    --artifacts "$portia_runtime_mode_output/BenchmarkDotNet.Artifacts" \
    > "$portia_runtime_mode_output/benchmarks.log" 2>&1
done
