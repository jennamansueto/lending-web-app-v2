#!/usr/bin/env bash
# Layer 1 parity gate: drives the Angular shell and its four federated remotes in a real browser
# and compares every rendered value against the golden corpus, the service and the system of
# record (see parity/l1/run-l1.mjs). Writes parity/reports/l1-ui.json, per-screen screenshots in
# parity/reports/l1-screens/ and a video of the run in parity/reports/l1-video/.
#
# The service API (5080) and the Angular dev server (4200 + remotes on 4201-4204) are started here
# unless they are already listening.
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
reports_dir="$repo_root/parity/reports"
api_url="${SERVICE_API_URL:-http://localhost:5080}"
ui_url="${UI_URL:-http://localhost:4200}"

mkdir -p "$reports_dir"
# Drop the previous report so a run that dies before the harness writes one cannot be mistaken
# for a pass.
rm -f "$reports_dir/l1-ui.json"
rm -rf "$reports_dir/l1-video"

api_pid=""
ui_pid=""
cleanup() {
    [ -n "$ui_pid" ] && kill "$ui_pid" 2>/dev/null
    [ -n "$api_pid" ] && kill "$api_pid" 2>/dev/null
    wait 2>/dev/null
}
trap cleanup EXIT

# Node is installed through nvm on the parity host, so it is not on PATH in a non-login shell.
if ! command -v node >/dev/null 2>&1 && [ -s "${NVM_DIR:-$HOME/.nvm}/nvm.sh" ]; then
    # shellcheck disable=SC1091
    . "${NVM_DIR:-$HOME/.nvm}/nvm.sh"
fi
if ! command -v node >/dev/null 2>&1; then
    echo "L1: node is required (see docs/parity.md)"
    exit 1
fi

wait_for() { # wait_for <label> <seconds> <command...>
    local label="$1" deadline=$(( SECONDS + $2 )); shift 2
    until "$@" >/dev/null 2>&1; do
        if [ $SECONDS -ge $deadline ]; then
            echo "L1: timed out waiting for $label"
            return 1
        fi
        sleep 2
    done
    echo "L1: $label ready"
}

if curl -fsS "$api_url/api/borrowers" >/dev/null 2>&1; then
    echo "L1: reusing the service API already listening on $api_url"
else
    echo "L1: starting the service API"
    dotnet run --project "$repo_root/src/Contoso.Lending.Api" -c Debug \
        >"$reports_dir/l1-api.log" 2>&1 &
    api_pid=$!
    wait_for "service API ($api_url)" 180 curl -fsS "$api_url/api/borrowers" || exit 1
fi

if curl -fsS "$ui_url" >/dev/null 2>&1; then
    echo "L1: reusing the Angular dev server already listening on $ui_url"
else
    echo "L1: starting the Angular shell and remotes"
    (cd "$repo_root/ui" && npx nx serve shell) >"$reports_dir/l1-ui-server.log" 2>&1 &
    ui_pid=$!
    wait_for "Angular shell ($ui_url)" 600 curl -fsS "$ui_url" || exit 1
fi

if [ ! -d "$repo_root/parity/l1/node_modules" ]; then
    echo "L1: installing the browser harness dependencies"
    (cd "$repo_root/parity/l1" && npm install --no-audit --no-fund) || exit 1
    (cd "$repo_root/parity/l1" && npx playwright install chromium) || exit 1
fi

SERVICE_API_URL="$api_url" UI_URL="$ui_url" node "$repo_root/parity/l1/run-l1.mjs"
run_exit=$?

if [ $run_exit -ne 0 ]; then
    echo "L1 PARITY FAILED"
    exit 1
fi
echo "L1 PARITY GREEN — zero tolerance, every screen matched the legacy behaviour"
