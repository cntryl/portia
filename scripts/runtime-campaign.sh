#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="${1:?Usage: runtime-campaign.sh OUTPUT}"
test ! -e "$output/source-commit.txt"
test -z "$(git -C "$root" status --porcelain)"
mkdir -p "$output"
git -C "$root" rev-parse HEAD > "$output/source-commit.txt"
dotnet --info > "$output/dotnet-info.txt"
dotnet restore "$root/bench/Portia.Benchmarks/Portia.Benchmarks.csproj" --locked-mode
dotnet build "$root/bench/Portia.Benchmarks/Portia.Benchmarks.csproj" -c Release --no-restore
dotnet run --project "$root/bench/Portia.Benchmarks/Portia.Benchmarks.csproj" -c Release --no-build -- \
  --runtime-qualification > "$output/controls.log" 2>&1
cd "$root"
dotnet run --project bench/Portia.Benchmarks/Portia.Benchmarks.csproj -c Release --no-build -- \
  --filter '*RequestLifecycle*' '*RequestContextAndPreflight*' '*RequestDispatchBenchmarks*' '*RequestTelemetryBenchmarks*' \
    '*AggregateLifecycle*' '*EnvelopeLifecycle*' '*AggregateRepositoryBenchmarks*' '*HttpLifecycle*' '*HttpBindingBenchmarks*' \
    '*MixedProcessorLifecycle*' '*MixedReactorLifecycle*' '*ReactorDispatchBenchmarks*' \
  --iterationCount 20 --warmupCount 5 --launchCount 1 --iterationTime 250 \
  --outliers DontRemove --keepFiles --allStats \
  --exporters json csv --artifacts "$output/BenchmarkDotNet.Artifacts" > "$output/campaign.log" 2>&1
