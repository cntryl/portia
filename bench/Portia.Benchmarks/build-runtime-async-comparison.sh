#!/usr/bin/env bash
set -euo pipefail

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)
project=bench/Portia.Benchmarks/Portia.Benchmarks.csproj
output=${1:?Usage: build-runtime-async-comparison.sh /absolute/new-evidence-directory}
cd "$repo"

if [[ "$output" != /* || -e "$output" ]]; then
  echo "Use a new absolute evidence directory outside the repository." >&2
  exit 1
fi
parent=$(cd "$(dirname "$output")" && pwd -P)
output="${parent%/}/$(basename "$output")"
if [[ "$output/" == "$repo/"* ]]; then
  echo "Use a new absolute evidence directory outside the repository." >&2
  exit 1
fi
if [[ -n $(git status --porcelain) ]]; then
  echo "Build both variants from a clean, committed checkout." >&2
  exit 1
fi

mkdir "$output"
git rev-parse HEAD > "$output/source-commit.txt"
dotnet --info > "$output/dotnet-info.txt"
dotnet --list-runtimes > "$output/dotnet-runtimes.txt"
uname -a > "$output/uname.txt"
dotnet restore "$project" > "$output/restore.log" 2>&1

for mode in off on; do
  variant="$output/$mode"
  features="runtime-async=$mode"
  mkdir "$variant"
  dotnet build "$project" --configuration Release --no-restore --no-incremental \
    --output "$variant/bin" -p:Features="$features" --verbosity normal \
    > "$variant/build.log" 2>&1

  # Validate actual compiler invocations. A global MSBuild property applies to the project
  # references as well as the benchmark executable and overrides the normal build default.
  rg '/csc(\.dll)?"? /noconfig ' "$variant/build.log" > "$variant/compilers.txt"
  if rg -v -Fq -- "/features:$features" "$variant/compilers.txt" ||
      ! rg -Fq '/out:obj/Release/net11.0/Portia.Benchmarks.dll' "$variant/compilers.txt" ||
      ! rg -Fq '/out:obj/Release/net11.0/Portia.Core.dll' "$variant/compilers.txt"; then
    echo "Compiler invocations did not prove the $mode variant. See $variant/build.log." >&2
    exit 1
  fi

  (
    cd "$variant/bin"
    test -f Portia.Benchmarks.dll
    test -f Portia.Benchmarks.deps.json
    test -f Portia.Benchmarks.runtimeconfig.json
    find . -type f -exec shasum -a 256 {} + | LC_ALL=C sort > "$variant/sha256.txt"
  )
done

for mode in off on; do
  (cd "$output/$mode/bin" && shasum -a 256 -c ../sha256.txt) > "$output/$mode/verify.log"
done
cmp "$output/off/bin/Portia.Benchmarks.deps.json" "$output/on/bin/Portia.Benchmarks.deps.json"
cmp "$output/off/bin/Portia.Benchmarks.runtimeconfig.json" "$output/on/bin/Portia.Benchmarks.runtimeconfig.json"
if cmp -s "$output/off/bin/Portia.Core.dll" "$output/on/bin/Portia.Core.dll"; then
  echo "The Runtime Async variants produced identical Portia.Core binaries." >&2
  exit 1
fi
printf '%s\n' "Built both variants. Verify sha256.txt before collecting any samples."
