#!/usr/bin/env bash
# End-to-end demo for the workflow layer (layer 3).
#
# Starts a Temporal dev server and the workflow worker, then runs the APPROVED and DECLINED
# loan-application scenarios taken from the legacy golden corpus
# (parity/golden/BR-ELG-009_eligibility_decision.json) and asserts the service-produced
# resultText byte-for-byte.
#
# Prerequisites (see docs/workflow-layer.md):
#   * Postgres up and loaded (docker compose up -d postgres; tools/migrator, or the demo seed)
#   * the service API reachable at $LENDING_API_BASE (default http://localhost:5080)
#     start it with: dotnet run --project src/Contoso.Lending.Api
#
# Usage: src/Contoso.Lending.Workflow/scripts/run-demo.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
compose_file="$repo_root/src/Contoso.Lending.Workflow/docker-compose.workflow.yml"
export TEMPORAL_ADDRESS="${TEMPORAL_ADDRESS:-localhost:7233}"
export LENDING_API_BASE="${LENDING_API_BASE:-http://localhost:5080}"

log() { printf '\n=== %s\n' "$*"; }

log "service API check: $LENDING_API_BASE"
if ! curl -fsS -o /dev/null -X POST "$LENDING_API_BASE/api/eligibility/evaluate" \
  -H 'content-type: application/json' \
  -d '{"borrowerId":1,"productType":"TERM","amount":100000,"termMonths":60,"annualIncome":600000,"monthlyDebt":5000,"creditScore":700,"collateralValue":200000,"yearsInBusiness":5}'; then
  echo "service API is not answering at $LENDING_API_BASE — start it with:" >&2
  echo "  dotnet run --project src/Contoso.Lending.Api" >&2
  exit 1
fi

log "temporal dev server"
docker compose -f "$compose_file" up -d --wait

worker_log="$(mktemp -t workflow-worker-XXXXXX.log)"
log "worker (log: $worker_log)"
dotnet run --project "$repo_root/src/Contoso.Lending.Workflow" -- worker >"$worker_log" 2>&1 &
worker_pid=$!
trap 'kill "$worker_pid" 2>/dev/null || true' EXIT

for _ in $(seq 1 60); do
  grep -q "Application started\|Now listening\|Starting worker" "$worker_log" 2>/dev/null && break
  sleep 1
done
sleep 2

log "scenarios"
set +e
artifact_path="${PARITY_L4_ARTIFACT:-$repo_root/parity/artifacts/l4-scenarios.json}"
dotnet run --project "$repo_root/src/Contoso.Lending.Workflow" -- demo --artifact "$artifact_path"
status=$?
set -e

log "worker log tail"
tail -n 15 "$worker_log" || true
exit "$status"
