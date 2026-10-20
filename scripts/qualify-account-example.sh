#!/usr/bin/env bash
set -euo pipefail

portia_qualification_root=$(cd "$(dirname "$0")/.." && pwd)
portia_example_root="$portia_qualification_root/examples/Portia.Accounts"
portia_compose_file="$portia_example_root/compose.yaml"
portia_project="portia-accounts-qualification-$$"
portia_evidence_directory=${1:-"$portia_qualification_root/artifacts/account-otel"}
portia_package_version=${PORTIA_SMOKE_PACKAGE_VERSION:-0.8.1}
portia_test_project="$portia_example_root/Portia.Accounts.Tests/Portia.Accounts.Tests.csproj"
portia_api_project="$portia_example_root/Portia.Accounts.Api/Portia.Accounts.Api.csproj"
portia_worker_project="$portia_example_root/Portia.Accounts.Worker/Portia.Accounts.Worker.csproj"
portia_api_pid=
portia_worker_pid=
portia_nuget_packages=${NUGET_PACKAGES:-"$portia_evidence_directory/nuget"}

mkdir -p "$portia_evidence_directory"
export NUGET_PACKAGES="$portia_nuget_packages"

portia_compose() {
  docker compose --project-name "$portia_project" --file "$portia_compose_file" "$@"
}

portia_stop_host() {
  local pid=$1
  local log_file=$2
  local exit_code=0
  kill -TERM "$pid"
  if wait "$pid"; then
    return 0
  else
    exit_code=$?
  fi
  if [[ "$exit_code" == 143 ]] && grep -Eq 'Application is shutting down' "$log_file"; then
    return 0
  fi
  return "$exit_code"
}

portia_cleanup() {
  local exit_code=$?
  trap - EXIT
  set +e
  if [[ -n "$portia_api_pid" ]]; then
    kill -TERM "$portia_api_pid" 2>/dev/null
  fi
  if [[ -n "$portia_worker_pid" ]]; then
    kill -TERM "$portia_worker_pid" 2>/dev/null
  fi
  if [[ -n "$portia_api_pid" ]]; then
    wait "$portia_api_pid"
  fi
  if [[ -n "$portia_worker_pid" ]]; then
    wait "$portia_worker_pid"
  fi
  portia_compose logs --no-color otel-collector >"$portia_evidence_directory/otel-export.log" 2>&1
  portia_compose down --remove-orphans
  exit "$exit_code"
}
trap portia_cleanup EXIT

for portia_project_file in "$portia_test_project" "$portia_api_project" "$portia_worker_project"; do
  dotnet restore "$portia_project_file" \
    --configfile "$portia_example_root/NuGet.Config" \
    -p:PortiaSmokePackageVersion="$portia_package_version"
  dotnet build "$portia_project_file" --configuration Release --no-restore \
    -p:PortiaSmokePackageVersion="$portia_package_version"
done

dotnet test "$portia_test_project" --configuration Release --no-build --no-restore \
  -p:PortiaSmokePackageVersion="$portia_package_version"

portia_compose up -d --wait
export Fitz__Endpoint=ws://127.0.0.1:4090/ws
export Fitz__ApplicationName=portia-accounts
export Accounts__Tenant=demo
export OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4317
export OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=http://127.0.0.1:4317
export OTEL_EXPORTER_OTLP_METRICS_ENDPOINT=http://127.0.0.1:4317
export OTEL_EXPORTER_OTLP_LOGS_ENDPOINT=http://127.0.0.1:4317
export OTEL_EXPORTER_OTLP_PROTOCOL=grpc

dotnet "$portia_example_root/Portia.Accounts.Worker/bin/Release/net10.0/Portia.Accounts.Worker.dll" \
  >"$portia_evidence_directory/worker.log" 2>&1 &
portia_worker_pid=$!
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS=http://127.0.0.1:5088 \
  dotnet "$portia_example_root/Portia.Accounts.Api/bin/Release/net10.0/Portia.Accounts.Api.dll" \
  >"$portia_evidence_directory/api.log" 2>&1 &
portia_api_pid=$!

portia_account_id=01234567-89ab-4cde-8fab-0123456789ab
curl --fail --silent --show-error --retry 30 --retry-delay 1 --retry-connrefused \
  --max-time 2 --output /dev/null \
  -H 'X-Demo-Tenant: demo' -H "X-Demo-Account: $portia_account_id" \
  http://127.0.0.1:5088/openapi/v1.json

curl --fail-with-body --silent --show-error -X POST \
  -H 'Content-Type: application/json' -H 'X-Demo-Tenant: demo' -H "X-Demo-Account: $portia_account_id" \
  -d "{\"tenant\":\"demo\",\"account_id\":\"$portia_account_id\",\"initial_balance_cents\":500}" \
  "http://127.0.0.1:5088/tenants/demo/accounts/$portia_account_id/open" \
  >"$portia_evidence_directory/open-account.response"
curl --fail-with-body --silent --show-error -X POST \
  -H 'Content-Type: application/json' -H 'X-Demo-Tenant: demo' -H "X-Demo-Account: $portia_account_id" \
  -d "{\"tenant\":\"demo\",\"account_id\":\"$portia_account_id\",\"amount_cents\":125}" \
  "http://127.0.0.1:5088/tenants/demo/accounts/$portia_account_id/deposits" \
  >"$portia_evidence_directory/deposit.response"

portia_balance_ready=false
for _ in {1..30}; do
  portia_status=$(curl --silent --show-error --output "$portia_evidence_directory/account.response" \
    --write-out '%{http_code}' -H 'X-Demo-Tenant: demo' -H "X-Demo-Account: $portia_account_id" \
    "http://127.0.0.1:5088/tenants/demo/accounts/$portia_account_id")
  if [[ "$portia_status" == 200 ]] && grep -Eq '"balance_cents"[[:space:]]*:[[:space:]]*625' \
    "$portia_evidence_directory/account.response"; then
    portia_balance_ready=true
    break
  fi
  sleep 1
done
if [[ "$portia_balance_ready" != true ]]; then
  cat "$portia_evidence_directory/account.response"
  cat "$portia_evidence_directory/api.log"
  cat "$portia_evidence_directory/worker.log"
  exit 1
fi

portia_stop_host "$portia_api_pid" "$portia_evidence_directory/api.log"
portia_api_pid=
portia_stop_host "$portia_worker_pid" "$portia_evidence_directory/worker.log"
portia_worker_pid=

portia_compose logs --no-color otel-collector >"$portia_evidence_directory/otel-export.log" 2>&1
grep -Eq 'portia.request.execute' "$portia_evidence_directory/otel-export.log"
grep -Eq 'portia.request.duration' "$portia_evidence_directory/otel-export.log"
grep -Eq 'Account request lifecycle stage' "$portia_evidence_directory/otel-export.log"
grep -Eq 'Account projection worker started' "$portia_evidence_directory/otel-export.log"
grep -Eq 'portia.accounts.api' "$portia_evidence_directory/otel-export.log"
grep -Eq 'portia.accounts.worker' "$portia_evidence_directory/otel-export.log"
grep -Eqi 'Trace ID[[:space:]]*:[[:space:]]*[0-9a-f]{32}' "$portia_evidence_directory/otel-export.log"
grep -Eqi 'Span ID[[:space:]]*:[[:space:]]*[0-9a-f]{16}' "$portia_evidence_directory/otel-export.log"
awk '
  /LogRecord #[0-9]+/ {
    if (lifecycle && length(trace) == 32 && length(span) == 16) correlated = 1
    lifecycle = 0
    trace = ""
    span = ""
  }
  /Body: Str\(Account request lifecycle stage/ { lifecycle = 1 }
  /Trace ID:/ { trace = $0; sub(/^.*Trace ID: /, "", trace) }
  /Span ID:/ { span = $0; sub(/^.*Span ID: /, "", span) }
  END {
    if (lifecycle && length(trace) == 32 && length(span) == 16) correlated = 1
    exit !correlated
  }
' "$portia_evidence_directory/otel-export.log"

printf 'Account package and OTLP qualification passed. Evidence: %s\n' "$portia_evidence_directory"
