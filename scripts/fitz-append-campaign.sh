#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="${1:?Usage: fitz-append-campaign.sh OUTPUT [REPETITIONS=30]}"
repetitions="${2:-30}"
project="portia-performance-$(date -u +%Y%m%d%H%M%S)-$$"
compose=(docker compose -p "$project" -f "$root/bench/fitz-performance.compose.yaml")
test ! -e "$output/source-commit.txt"
test -z "$(git -C "$root" status --porcelain)"
mkdir -p "$output"
git -C "$root" rev-parse HEAD > "$output/source-commit.txt"
dotnet --info > "$output/dotnet-info.txt"
docker info --format '{{json .}}' > "$output/docker-info.json"
dotnet restore "$root/bench/Portia.Benchmarks/Portia.Benchmarks.csproj" --locked-mode
dotnet build "$root/bench/Portia.Benchmarks/Portia.Benchmarks.csproj" -c Release --no-restore
cleanup() { "${compose[@]}" down -v > "$output/cleanup.log" 2>&1; }
trap cleanup EXIT
"${compose[@]}" up -d
"${compose[@]}" config > "$output/broker-config.yaml"
container="$("${compose[@]}" ps -q fitz)"
docker inspect "$container" > "$output/broker-inspect.json"
utility_image="busybox:1.37.0@sha256:bdf57e528e45e4433820e045b29b4597825a1c9e38353532d90a01445013f82e"
# Fitz is distroless. Probe its actual persistent filesystem with an immutable utility image.
docker run --rm --volumes-from "$container":ro "$utility_image" df -k /data > "$output/storage-capacity.txt"
# The persistent Docker volume must have at least 5 GiB free; the fixed campaign writes <1 GiB.
available="$(awk 'NR==2 {print $4}' "$output/storage-capacity.txt")"
test "$available" -ge 5242880
endpoint="ws://127.0.0.1:${FITZ_PERFORMANCE_PORT:-40902}/ws"
dotnet run --project "$root/bench/Portia.Benchmarks/Portia.Benchmarks.csproj" -c Release --no-build -- \
  --fitz-append-campaign "$output" "$endpoint" "$repetitions" > "$output/campaign.log" 2>&1
"${compose[@]}" restart fitz
dotnet run --project "$root/bench/Portia.Benchmarks/Portia.Benchmarks.csproj" -c Release --no-build -- \
  --fitz-restart-readback "$output" "$endpoint" > "$output/restart-readback.log" 2>&1
"${compose[@]}" logs --no-color > "$output/broker.log"
