#!/usr/bin/env bash
# Layer 4 parity gate: end-to-end runs of the Temporal workflow against a live stack
# (Postgres + service API on 5080 + Temporal dev server + origination worker).
#
# Each scenario in parity/scenarios/ is executed as a real workflow; the final workflow
# result is compared, with zero tolerance, against the legacy expectations taken from the
# golden corpus in parity/golden/. Writes parity/reports/l4-e2e.json and exits non-zero on
# any mismatch.
#
# The database is written to (applications and loans are booked): this is the local dev
# stack, and `dotnet run --project tools/migrator -- migrate` resets it.
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
reports_dir="$repo_root/parity/reports"
runs_dir="$reports_dir/l4-runs"
report="$reports_dir/l4-e2e.json"
api_url="${SERVICE_API_URL:-http://localhost:5080}"
temporal_address="${TEMPORAL_ADDRESS:-localhost:7233}"
psql_docker=(docker exec -i lending-postgres psql -U lending -d lending -v ON_ERROR_STOP=1)

mkdir -p "$runs_dir"
rm -f "$runs_dir"/*.json

api_pid=""
worker_pid=""
cleanup() {
    [ -n "$worker_pid" ] && kill "$worker_pid" 2>/dev/null
    [ -n "$api_pid" ] && kill "$api_pid" 2>/dev/null
    wait 2>/dev/null
}
trap cleanup EXIT

wait_for() { # wait_for <label> <seconds> <command...>
    local label="$1" deadline=$(( SECONDS + $2 )); shift 2
    until "$@" >/dev/null 2>&1; do
        if [ $SECONDS -ge $deadline ]; then
            echo "L4: timed out waiting for $label"
            return 1
        fi
        sleep 1
    done
    echo "L4: $label ready"
}

echo "L4: starting postgres and temporal"
docker compose -f "$repo_root/docker-compose.yml" up -d postgres temporal || exit 1
wait_for "postgres" 120 docker exec lending-postgres pg_isready -U lending -d lending || exit 1
wait_for "temporal ($temporal_address)" 120 \
    docker exec lending-temporal temporal workflow list --address 127.0.0.1:7233 || exit 1

if ! "${psql_docker[@]}" -tAc "select to_regclass('public.borrower')" | grep -q borrower; then
    echo "L4: applying database/postgres/schema.sql"
    "${psql_docker[@]}" -f /database/postgres/schema.sql >/dev/null || exit 1
fi

# Scenario borrowers. The workflow passes the applicant details it was given to the service;
# these rows only satisfy the loan_application/loan foreign keys.
"${psql_docker[@]}" >/dev/null <<'SQL' || exit 1
INSERT INTO borrower (borrower_id, legal_name, tax_id, credit_score, deposit_balance, years_in_business)
VALUES (9001, 'Parity Scenario Borrower 9001', '99-9009001', 800, 0, 10),
       (9002, 'Parity Scenario Borrower 9002', '99-9009002', 800, 0, 10),
       (9003, 'Parity Scenario Borrower 9003', '99-9009003', 619, 0, 10)
ON CONFLICT (borrower_id) DO UPDATE
   SET credit_score = excluded.credit_score,
       deposit_balance = excluded.deposit_balance,
       years_in_business = excluded.years_in_business;
SQL

echo "L4: building the solution"
dotnet build "$repo_root/Contoso.Lending.sln" -c Debug --nologo -v quiet || exit 1

if curl -fsS "$api_url/api/borrowers" >/dev/null 2>&1; then
    echo "L4: reusing the service API already listening on $api_url"
else
    echo "L4: starting the service API"
    dotnet run --project "$repo_root/src/Contoso.Lending.Api" --no-build -c Debug \
        >"$reports_dir/l4-api.log" 2>&1 &
    api_pid=$!
    wait_for "service API ($api_url)" 120 curl -fsS "$api_url/api/borrowers" || exit 1
fi

echo "L4: starting the origination worker"
SERVICE_API_URL="$api_url" TEMPORAL_ADDRESS="$temporal_address" \
    dotnet run --project "$repo_root/src/Contoso.Lending.Workflow" --no-build -c Debug -- worker \
    >"$reports_dir/l4-worker.log" 2>&1 &
worker_pid=$!
wait_for "worker task queue" 120 grep -q "task queue" "$reports_dir/l4-worker.log" || exit 1

run_exit=0
for scenario in "$repo_root"/parity/scenarios/*.json; do
    name="$(basename "$scenario" .json)"
    input="$runs_dir/$name.input.json"
    python3 -c "import json,sys; json.dump(json.load(open(sys.argv[1]))['input'], open(sys.argv[2],'w'))" \
        "$scenario" "$input" || exit 1
    echo "L4: running scenario $name"
    TEMPORAL_ADDRESS="$temporal_address" \
        dotnet run --project "$repo_root/src/Contoso.Lending.Workflow" --no-build -c Debug -- \
        start --input "$input" --out "$runs_dir/$name.run.json" \
        --id "l4-$name-$(date +%s)" --reviewer "parity-l4" >/dev/null
    if [ $? -ne 0 ]; then
        echo "L4: scenario $name failed to execute"
        run_exit=1
    fi
done

REPO_ROOT="$repo_root" RUNS_DIR="$runs_dir" REPORT="$report" python3 - <<'PY'
import json, os, sys
from decimal import Decimal

repo_root = os.environ["REPO_ROOT"]
runs_dir = os.environ["RUNS_DIR"]
report_path = os.environ["REPORT"]
scenarios_dir = os.path.join(repo_root, "parity", "scenarios")
golden_dir = os.path.join(repo_root, "parity", "golden")


def load(path):
    with open(path) as handle:
        return json.load(handle, parse_float=Decimal)


def golden_schedule(spec):
    records = load(os.path.join(golden_dir, spec["golden"]))
    for record in records:
        if all(record["input"].get(key) == value for key, value in spec["match"].items()):
            return record["expected"]
    raise SystemExit("no golden record in %s matches %s" % (spec["golden"], spec["match"]))


def same(expected, actual):
    if expected is None or actual is None:
        return expected is None and actual is None
    if isinstance(expected, (Decimal, int)) and not isinstance(expected, bool):
        return Decimal(str(actual)) == Decimal(str(expected))
    return expected == actual


def jsonable(value):
    if isinstance(value, Decimal):
        return float(value)
    if isinstance(value, dict):
        return {key: jsonable(item) for key, item in value.items()}
    if isinstance(value, list):
        return [jsonable(item) for item in value]
    return value


scenarios = sorted(
    os.path.join(scenarios_dir, name)
    for name in os.listdir(scenarios_dir)
    if name.endswith(".json")
)

report = {"layer": "l4-e2e", "scenarios": [], "totals": {}}
failed = 0

for path in scenarios:
    scenario = load(path)
    name = scenario["name"]
    run_path = os.path.join(runs_dir, "%s.run.json" % name)
    entry = {
        "scenario": name,
        "description": scenario["description"],
        "inputs": jsonable(scenario["input"]),
        "expected": jsonable(scenario["expected"]),
        "actual": None,
        "mismatches": [],
        "pass": False,
    }
    report["scenarios"].append(entry)

    if not os.path.exists(run_path):
        entry["mismatches"].append("the workflow produced no result (%s missing)" % run_path)
        failed += 1
        continue

    run = load(run_path)
    result = run["result"]
    status = run["status"]
    schedule = result.get("schedule") or []
    actual = {
        "decision": result["decision"],
        "declineReason": result["declineReason"],
        "firedRuleId": result["firedRuleId"],
        "dti": result["dti"],
        "ltv": result["ltv"],
        "estimatedPayment": result["estimatedPayment"],
        "resultText": result["resultText"],
        "rate": result["rate"],
        "originationFee": result["originationFee"],
        "payment": result["payment"],
        "scheduleRows": len(schedule) or None,
        "booked": result["loanId"] is not None,
    }
    entry["actual"] = jsonable(actual)
    entry["workflow"] = {
        "workflowId": run["workflowId"],
        "runId": run["runId"],
        "stage": status["stage"],
        "storedDecision": status["decision"],
        "appId": result["appId"],
        "loanId": result["loanId"],
        "reviewers": status["reviewers"],
    }

    for key, expected_value in scenario["expected"].items():
        actual_value = actual.get(key)
        if not same(expected_value, actual_value):
            entry["mismatches"].append(
                "%s: expected %s, got %s" % (key, jsonable(expected_value), jsonable(actual_value))
            )

    if "expectedSchedule" in scenario:
        expected_rows = golden_schedule(scenario["expectedSchedule"])
        entry["expected"]["scheduleRows"] = len(expected_rows)
        if len(expected_rows) != len(schedule):
            entry["mismatches"].append(
                "schedule: expected %d rows, got %d" % (len(expected_rows), len(schedule))
            )
        else:
            for expected_row, actual_row in zip(expected_rows, schedule):
                for key in ("Period", "Payment", "Interest", "Principal", "Balance"):
                    if not same(expected_row[key], actual_row[key[0].lower() + key[1:]]):
                        entry["mismatches"].append(
                            "schedule period %s %s: expected %s, got %s"
                            % (
                                expected_row["Period"],
                                key,
                                jsonable(expected_row[key]),
                                jsonable(actual_row[key[0].lower() + key[1:]]),
                            )
                        )

    if status["decision"] != result["decision"]:
        entry["mismatches"].append(
            "GetStatus query decision %s != workflow result decision %s"
            % (status["decision"], result["decision"])
        )
    if run["reviewerRecorded"] and "parity-l4" not in status["reviewers"]:
        entry["mismatches"].append("the reviewer signal was accepted but is not in GetStatus")

    entry["pass"] = not entry["mismatches"]
    failed += 0 if entry["pass"] else 1

report["totals"] = {
    "total": len(scenarios),
    "passed": len(scenarios) - failed,
    "failed": failed,
}

os.makedirs(os.path.dirname(report_path), exist_ok=True)
with open(report_path, "w") as handle:
    json.dump(report, handle, indent=2)
    handle.write("\n")

print("")
print("Layer 4 parity — end-to-end workflow runs")
print("%-26s %-9s %-10s %s" % ("SCENARIO", "DECISION", "RESULT", "LOAN"))
for entry in report["scenarios"]:
    actual = entry["actual"] or {}
    print(
        "%-26s %-9s %-10s %s"
        % (
            entry["scenario"],
            actual.get("decision") or "-",
            "PASS" if entry["pass"] else "FAIL",
            (entry.get("workflow") or {}).get("loanId") or "-",
        )
    )
    for mismatch in entry["mismatches"]:
        print("    %s" % mismatch)
print("%-26s %d/%d passed" % ("TOTAL", report["totals"]["passed"], report["totals"]["total"]))
print("report: %s" % report_path)
print("")
sys.exit(1 if failed else 0)
PY
compare_exit=$?

if [ $run_exit -ne 0 ] || [ $compare_exit -ne 0 ]; then
    echo "L4 PARITY FAILED"
    exit 1
fi
echo "L4 PARITY GREEN — zero tolerance, every scenario matched the legacy expectations"
