#!/usr/bin/env bash
set -euo pipefail

portia_output_identity_repo=$(cd "$(dirname "$0")/.." && pwd)
portia_output_identity_output=${1:?Provide a new evidence directory}
if [[ -e "$portia_output_identity_output" ]]; then
  echo "Use a fresh evidence directory to preserve prior results." >&2
  exit 1
fi
mkdir -p "$(dirname "$portia_output_identity_output")"
portia_output_identity_output=$(cd "$(dirname "$portia_output_identity_output")" && pwd)/$(basename "$portia_output_identity_output")
mkdir -p "$portia_output_identity_output/baseline" "$portia_output_identity_output/current"

portia_output_identity_baseline_source=7d7e3c4
portia_output_identity_baseline_patch="$portia_output_identity_repo/docs/qualification/compiler-net11-2026-11-11/baseline/baseline-harness.patch"
portia_output_identity_baseline_root=$(mktemp -d "${TMPDIR:-/tmp}/portia-output-identity.XXXXXX")
portia_output_identity_baseline_worktree="$portia_output_identity_baseline_root/source"
portia_output_identity_cleanup() {
  git -C "$portia_output_identity_repo" worktree remove --force "$portia_output_identity_baseline_worktree" >/dev/null 2>&1 || true
  rmdir "$portia_output_identity_baseline_root" >/dev/null 2>&1 || true
}
trap portia_output_identity_cleanup EXIT
git -C "$portia_output_identity_repo" worktree add --detach "$portia_output_identity_baseline_worktree" "$portia_output_identity_baseline_source"
git -C "$portia_output_identity_baseline_worktree" apply "$portia_output_identity_baseline_patch"
cp "$portia_output_identity_repo/bench/Portia.CompilerBenchmarks/Program.cs" \
  "$portia_output_identity_baseline_worktree/bench/Portia.CompilerBenchmarks/Program.cs"

portia_output_identity_harness_hash=$(shasum -a 256 "$portia_output_identity_repo/bench/Portia.CompilerBenchmarks/Program.cs" | awk '{print $1}')
portia_output_identity_current_source=$(git -C "$portia_output_identity_repo" rev-parse HEAD)

portia_output_identity_build_campaign_source() {
  local portia_source_root=$1
  local portia_label=$2
  local portia_json_generator_refs="$portia_output_identity_output/$portia_label/resolved-analyzers.json"
  (cd "$portia_source_root" && dotnet build eng/Portia.RepositoryTools --configuration Release) \
    > "$portia_output_identity_output/$portia_label/repository-tools-build.log"
  if [[ "$portia_label" == "baseline" ]]; then
    local portia_analyzer_failure_log="$portia_output_identity_output/$portia_label/analyzer-enabled-build-failure.log"
    set +e
    (cd "$portia_source_root" && dotnet build bench/Portia.CompilerBenchmarks --configuration Release) \
      > "$portia_analyzer_failure_log" 2>&1
    local portia_analyzer_build_status=$?
    set -e
    printf '%s\n' "$portia_analyzer_build_status" > "$portia_output_identity_output/$portia_label/analyzer-enabled-build-exit-code.txt"
    if [[ "$portia_analyzer_build_status" -eq 0 ]] || ! rg -q 'error RS0025' "$portia_analyzer_failure_log"; then
      echo "Expected the historical analyzer-enabled baseline build to fail with RS0025; see $portia_analyzer_failure_log." >&2
      exit 1
    fi
    # The historical source has duplicate PublicAPI entries. The generator driver is invoked
    # directly by the harness, so disable build-time analyzers only for this baseline compilation.
    (cd "$portia_source_root" && dotnet build bench/Portia.CompilerBenchmarks --configuration Release -p:RunAnalyzers=false) \
      > "$portia_output_identity_output/$portia_label/compiler-build.log"
  else
    (cd "$portia_source_root" && dotnet build bench/Portia.CompilerBenchmarks --configuration Release) \
      > "$portia_output_identity_output/$portia_label/compiler-build.log"
  fi
  if [[ "$portia_label" == "current" ]]; then
    (cd "$portia_source_root" && dotnet msbuild bench/Portia.CompilerBenchmarks/Portia.CompilerBenchmarks.csproj \
      -target:ResolveReferences -getItem:Analyzer -verbosity:quiet) > "$portia_json_generator_refs"
  else
    printf '%s\n' "The historical net10.0 analyzer list has no SDK JSON generator; the harness uses the identical .NET 11 generator resolved from current." \
      > "$portia_output_identity_output/$portia_label/resolved-analyzers-note.txt"
  fi
}

portia_output_identity_build_campaign_source "$portia_output_identity_baseline_worktree" baseline
portia_output_identity_build_campaign_source "$portia_output_identity_repo" current
portia_output_identity_current_json_generator=$(cd "$portia_output_identity_repo" && \
  dotnet run --project eng/Portia.RepositoryTools --configuration Release -- \
  resolve-json-generator "$portia_output_identity_output/current/resolved-analyzers.json")
portia_output_identity_baseline_json_generator=$portia_output_identity_current_json_generator
mkdir -p "$portia_output_identity_output/assemblies/shared"
cp "$portia_output_identity_current_json_generator" \
  "$portia_output_identity_output/assemblies/shared/System.Text.Json.SourceGeneration.dll"
printf '%s\n' "$portia_output_identity_current_json_generator" > "$portia_output_identity_output/current/json-generator-path.txt"
printf '%s\n' "same .NET 11 SDK generator used by both revisions" \
  > "$portia_output_identity_output/baseline/json-generator-resolution.txt"

(cd "$portia_output_identity_baseline_worktree" && dotnet --info) > "$portia_output_identity_output/baseline/dotnet-info.txt"
(cd "$portia_output_identity_repo" && dotnet --info) > "$portia_output_identity_output/current/dotnet-info.txt"

(cd "$portia_output_identity_baseline_worktree" && DOTNET_ROLL_FORWARD=Major \
  PORTIA_SOURCE_COMMIT="$portia_output_identity_baseline_source" PORTIA_HARNESS_HASH="$portia_output_identity_harness_hash" \
  dotnet run --project bench/Portia.CompilerBenchmarks --configuration Release --no-build -- \
  output-identity "$portia_output_identity_output/baseline" "$portia_output_identity_baseline_json_generator")
(cd "$portia_output_identity_repo" && PORTIA_SOURCE_COMMIT="$portia_output_identity_current_source" \
  PORTIA_HARNESS_HASH="$portia_output_identity_harness_hash" \
  dotnet run --project bench/Portia.CompilerBenchmarks --configuration Release --no-build -- \
  output-identity "$portia_output_identity_output/current" "$portia_output_identity_current_json_generator")

mkdir -p "$portia_output_identity_output/source/baseline" "$portia_output_identity_output/source/current" \
  "$portia_output_identity_output/source/harness" "$portia_output_identity_output/assemblies/baseline" \
  "$portia_output_identity_output/assemblies/current"
portia_output_identity_missing_baseline_sources="$portia_output_identity_output/source/baseline/missing-files.txt"
git -C "$portia_output_identity_baseline_worktree" rev-parse HEAD > "$portia_output_identity_output/source/baseline/commit.txt"
git -C "$portia_output_identity_repo" rev-parse HEAD > "$portia_output_identity_output/source/current/commit.txt"
for portia_source_file in \
  src/Portia.Generators/DomainEvents/DomainEventCatalogGenerator.cs \
  src/Portia.Generators/DomainEvents/ProjectorReactorEventDispatcherGenerator.cs \
  src/Portia.Generators/Components/RegistrationCallInterceptorGenerator.cs \
  src/Portia.Generators/Infrastructure/GeneratedSource.cs; do
  mkdir -p "$portia_output_identity_output/source/baseline/$(dirname "$portia_source_file")" \
    "$portia_output_identity_output/source/current/$(dirname "$portia_source_file")"
  if [[ -f "$portia_output_identity_baseline_worktree/$portia_source_file" ]]; then
    cp "$portia_output_identity_baseline_worktree/$portia_source_file" \
      "$portia_output_identity_output/source/baseline/$portia_source_file"
  else
    printf '%s\n' "$portia_source_file" >> "$portia_output_identity_missing_baseline_sources"
  fi
  cp "$portia_output_identity_repo/$portia_source_file" \
    "$portia_output_identity_output/source/current/$portia_source_file"
done
cp "$portia_output_identity_repo/bench/Portia.CompilerBenchmarks/Program.cs" \
  "$portia_output_identity_output/source/harness/Program.cs"
cp "$portia_output_identity_repo/scripts/verify-compiler-output-identity.py" \
  "$portia_output_identity_output/source/harness/verify-compiler-output-identity.py"
cp "$portia_output_identity_repo/scripts/compiler-output-identity-campaign.sh" \
  "$portia_output_identity_output/source/harness/compiler-output-identity-campaign.sh"
cp "$portia_output_identity_baseline_patch" "$portia_output_identity_output/source/baseline-harness.patch"
for portia_generator_assembly in Portia.Generators Portia.AspNetCore.Generators; do
  cp "$portia_output_identity_baseline_worktree/src/$portia_generator_assembly/bin/Release/netstandard2.0/$portia_generator_assembly.dll" \
    "$portia_output_identity_output/assemblies/baseline/$portia_generator_assembly.dll"
  cp "$portia_output_identity_repo/src/$portia_generator_assembly/bin/Release/netstandard2.0/$portia_generator_assembly.dll" \
    "$portia_output_identity_output/assemblies/current/$portia_generator_assembly.dll"
done

python3 "$portia_output_identity_repo/scripts/verify-compiler-output-identity.py" "$portia_output_identity_output"
tar -czf "$portia_output_identity_output/raw-evidence.tar.gz" -C "$portia_output_identity_output" \
  baseline current generated-source-content source assemblies cross-version.json
