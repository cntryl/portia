#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 4 || ( "$1" != inspect && "$1" != run ) || ( "$3" != off && "$3" != on ) ]]; then
  echo "Usage: run-runtime-async-process.sh {inspect|run} BUILD_DIRECTORY {off|on} /absolute/new-process-directory" >&2
  exit 1
fi

action=$1
build=$(cd "$2" && pwd -P)
mode=$3
output=$4
if [[ "$output" != /* || -e "$output" ]]; then
  echo "Use a new absolute process directory outside the build directory." >&2
  exit 1
fi
parent=$(cd "$(dirname "$output")" && pwd -P)
output="${parent%/}/$(basename "$output")"
if [[ "$output/" == "$build/"* ]]; then
  echo "Use a new absolute process directory outside the build directory." >&2
  exit 1
fi

variant="$build/$mode"
mkdir "$output"
cp "$build/source-commit.txt" "$output/source-commit.txt"
cp "$variant/sha256.txt" "$output/expected-sha256.txt"
printf '%s\n' "$mode" > "$output/mode.txt"
cd "$variant/bin"

verify_input() {
  local manifest=$1
  if [[ -n $(find . ! -type f ! -type d -print) ]]; then
    echo "The frozen build contains file links or special files." >&2
    return 1
  fi
  find . -type f -exec shasum -a 256 {} + | LC_ALL=C sort > "$manifest"
  cmp "$output/expected-sha256.txt" "$manifest"
}

verify_input "$output/before-sha256.txt"

# The runtimeconfig selects the shared framework versions. Avoid runtime roll-forward and
# ambient .NET extensions so each launch uses the same declared application inputs.
export DOTNET_ROLL_FORWARD=Disable
unset DOTNET_ADDITIONAL_DEPS DOTNET_STARTUP_HOOKS DOTNET_SHARED_STORE
unset PORTIA_OPERATIONAL_BASELINE_DIRECT PORTIA_OPERATIONAL_WORKER
dotnet_host=$(command -v dotnet)
"$dotnet_host" --list-runtimes > "$output/dotnet-runtimes.txt"
uname -a > "$output/uname.txt"

args=("$PWD/Portia.Benchmarks.dll" --filter
  '*RequestDispatchBenchmarks*' '*HttpLifecycleBenchmarks.Json*' '*ProcessorDispatchBenchmarks*')
if [[ "$action" == inspect ]]; then
  args+=(--list flat)
else
  args+=(--inProcess --job short --outliers DontRemove --allStats --exporters json --keepFiles
    --artifacts "$output/BenchmarkDotNet.Artifacts")
fi
printf '%s\n' "$dotnet_host" "${args[@]}" > "$output/arguments.txt"
date -u +%FT%TZ > "$output/started-at.txt"
status=0
"$dotnet_host" "${args[@]}" > "$output/process.log" 2>&1 || status=$?
date -u +%FT%TZ > "$output/finished-at.txt"
printf '%s\n' "$status" > "$output/exit-code.txt"
verify_input "$output/after-sha256.txt"
exit "$status"
