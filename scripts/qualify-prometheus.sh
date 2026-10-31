#!/usr/bin/env bash
set -euo pipefail
portia_qualification_root=$(cd "$(dirname "$0")/.." && pwd)
portia_prometheus_container="portia-prometheus-qualification-$$"
portia_evidence_directory=${1:-"$portia_qualification_root/artifacts/prometheus"}
trap 'docker rm -f -v "$portia_prometheus_container" >/dev/null 2>&1 || true' EXIT
docker run --detach --name "$portia_prometheus_container" \
  --publish 127.0.0.1:49090:9090 \
  --mount "type=bind,source=$portia_qualification_root/bench/Portia.Benchmarks/Telemetry/prometheus.yaml,target=/etc/prometheus/prometheus.yml,readonly" \
  prom/prometheus:v3.15.1@sha256:fa3302279710be2f21a8301ec17a8b4f2301fd829bd113839b3b6b02b6f191a2 \
  --config.file=/etc/prometheus/prometheus.yml --web.enable-otlp-receiver \
  --storage.tsdb.retention.time=1h
curl --fail --silent --show-error --retry 30 --retry-delay 1 --retry-all-errors --retry-connrefused \
  --max-time 2 http://127.0.0.1:49090/-/ready >/dev/null
dotnet run --project "$portia_qualification_root/bench/Portia.Benchmarks" \
  --configuration Release --no-restore -- --prometheus-qualification "$portia_evidence_directory"
