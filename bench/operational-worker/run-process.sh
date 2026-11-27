#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat >&2 <<'USAGE'
Usage:
  run-process.sh prepare SOURCE_WORKTREE /absolute/new-build-directory
  run-process.sh {smoke|run} BUILD_DIRECTORY /absolute/new-evidence-directory MACHINE [HOST_EVIDENCE]
USAGE
  exit 2
}

[[ $# -ge 3 ]] || usage
mode=$1
input=$(cd "$2" && pwd -P)
output=$3
[[ "$output" == /* && ! -e "$output" ]] || usage
parent=$(cd "$(dirname "$output")" && pwd -P)
output="$parent/$(basename "$output")"
[[ "$output/" != "$input/"* ]] || usage

if [[ "$mode" == prepare ]]; then
  [[ $# == 3 ]] || usage
else
  [[ "$mode" == smoke || "$mode" == run ]] || usage
  [[ $# -ge 4 && $# -le 5 && -n "$4" ]] || usage
  machine=$4
  host_evidence=${5:-}
  if [[ "$mode" == run && ! -s "$host_evidence" ]]; then
    echo "A full run requires the accepted host evidence for this process." >&2
    exit 2
  fi
fi

mkdir "$output"
runner_revision=$(git -C "$(dirname "${BASH_SOURCE[0]}")" rev-parse HEAD)
cp "$0" "$output/run-process.sh"
printf '%s\n' "$runner_revision" > "$output/runner-revision.txt"

inventory() {
  find "$@" -type f -exec shasum -a 256 {} + | LC_ALL=C sort
}

if [[ "$mode" == prepare ]]; then
  cd "$input"
  revision=$(git rev-parse HEAD)
  mkdir -p "$output/overlay"
  printf '%s\n' "$revision" > "$output/revision.txt"
  git diff --binary HEAD > "$output/overlay/tracked.patch"
  git ls-files --others --exclude-standard -z |
    while IFS= read -r -d '' path; do
      case "$path" in
        bench/Portia.Benchmarks/Operations/OperationalWorkerBenchmark.cs|bench/Portia.Benchmarks/Operations/OperationalWorkerFixture.cs)
          mkdir -p "$output/overlay/$(dirname "$path")"
          cp "$path" "$output/overlay/$path"
          ;;
        *)
          echo "Unexpected untracked file in the benchmark worktree: $path" >&2
          exit 1
          ;;
      esac
    done
  (cd "$output/overlay" && inventory .) > "$output/overlay-sha256.txt"
  dotnet --info > "$output/dotnet-info.txt"
  dotnet --version > "$output/sdk.txt"
  dotnet build bench/Portia.Benchmarks/Portia.Benchmarks.csproj -c Release --no-incremental \
    --output "$output/bin" > "$output/build.log" 2>&1
  git diff --binary HEAD | cmp "$output/overlay/tracked.patch" -
  [[ "$(git rev-parse HEAD)" == "$revision" ]]
  while IFS= read -r -d '' path; do
    cmp "$path" "$output/overlay/$path"
  done < <(git ls-files --others --exclude-standard -z)
  (cd "$output/bin" && inventory .) > "$output/bin-sha256.txt"
  printf '%s\n' "$output"
  exit 0
fi

(cd "$input/overlay" && inventory .) | cmp "$input/overlay-sha256.txt" -
(cd "$input/bin" && inventory .) | cmp "$input/bin-sha256.txt" -
cp "$input"/{revision,sdk,bin-sha256,overlay-sha256}.txt "$output"
printf '%s\n' "$input" > "$output/build-directory.txt"
if [[ -n "$host_evidence" ]]; then
  cp "$host_evidence" "$output/host-evidence"
fi
revision=$(cat "$input/revision.txt")
sdk=$(cat "$input/sdk.txt")
broker_image=ghcr.io/cntryl/fitz@sha256:6d4f121fd772c17ef059c15f8653488ae8466ed30c6e5ef789b01ce65e395f30
endpoint=ws://127.0.0.1:40939/ws
docker image inspect "$broker_image" --format '{{.Id}} {{.Os}} {{.Architecture}}' > "$output/broker-image.txt"

# Each process gets an isolated Fitz storage directory. Never reuse or stop another run's broker.
broker_id=
cleanup() {
  if [[ -n "$broker_id" ]]; then
    docker logs "$broker_id" > "$output/broker.log" 2>&1 || true
    docker stop "$broker_id" >> "$output/broker-stop.log" 2>&1 || true
  fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
broker_id=$(docker run --rm -d -p 127.0.0.1:40939:4090 \
  -e FITZ_ADMIN_AUTH_MODE=open -e FITZ_AUTH_REQUIRED=false -e FITZ_HTTP_PORT=4090 \
  -e FITZ_STORAGE_MODE=local -e FITZ_STORAGE_PATH=/data/fitz -e FITZ_TCP_ENABLED=false "$broker_image")
docker inspect "$broker_id" --format '{{json .HostConfig.PortBindings}} {{json .Mounts}}' > "$output/broker-storage.txt"
docker info --format '{{.Driver}}' >> "$output/broker-storage.txt"

export PORTIA_OPERATIONAL_WORKER="$mode"
export FITZ_TEST_ENDPOINT="$endpoint"
export PORTIA_OPERATIONAL_WORKER_ARTIFACT="$output/result.json"
export PORTIA_OPERATIONAL_WORKER_COMMIT="$revision"
export PORTIA_OPERATIONAL_WORKER_SDK="$sdk"
export PORTIA_OPERATIONAL_WORKER_BROKER="$broker_image"
export PORTIA_OPERATIONAL_WORKER_MACHINE="$machine"
export DOTNET_ROLL_FORWARD=Disable
unset DOTNET_ADDITIONAL_DEPS DOTNET_STARTUP_HOOKS DOTNET_SHARED_STORE
dotnet --list-runtimes > "$output/dotnet-runtimes.txt"
date -u +%FT%TZ > "$output/started-at.txt"
status=0
dotnet "$input/bin/Portia.Benchmarks.dll" > "$output/process.log" 2>&1 || status=$?
printf '%s\n' "$status" > "$output/exit-code.txt"
date -u +%FT%TZ > "$output/finished-at.txt"
(cd "$input/bin" && inventory .) > "$output/bin-after-sha256.txt"
cmp "$output/bin-sha256.txt" "$output/bin-after-sha256.txt"
if [[ "$status" != 0 ]]; then
  echo "Benchmark process failed. See $output/process.log." >&2
  exit "$status"
fi
test -s "$output/result.json"
echo "$output/result.json"
