#!/usr/bin/env bash
# Run selected parity levels and aggregate their structured artifacts into
# parity/parity-dashboard.json and parity/parity-dashboard.md.
#
# JSON shape (specVersion 1.0):
# {
#   specVersion, startedAt, finishedAt, ok,
#   levels: [
#     { id: "L2", name, status, durationMs, summary, groups },
#     { id: "L3", name, status, durationMs, summary, rows },
#     { id: "L4", name, status, durationMs, summary, scenarios }
#   ]
# }
# L2 writes parity/artifacts/l2-cases.json from the test suite itself.
# L3 writes parity/artifacts/l3-report.json from migrator verify --report.
# L4 writes parity/artifacts/l4-scenarios.json from the demo runner itself.
# PARITY_LEVELS is a comma-separated subset of L2,L3,L4; default: all three.
# This script only reads those artifacts; it never scrapes tool logs.
# The command exits non-zero when any selected level fails.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
artifact_dir="$repo_root/parity/artifacts"
dashboard="$repo_root/parity/parity-dashboard.json"
snapshot="$repo_root/parity/parity-dashboard.md"
mkdir -p "$artifact_dir"

levels="${PARITY_LEVELS:-L2,L3,L4}"
IFS=',' read -r -a requested <<< "$levels"
declare -A selected
for level in "${requested[@]}"; do
  level="${level//[[:space:]]/}"
  case "$level" in
    L2|L3|L4) selected["$level"]=1 ;;
    '') ;;
    *) echo "unknown parity level: $level" >&2; exit 2 ;;
  esac
done

started_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
declare -A status_codes durations

run_level() {
  local id="$1"
  local start_ns end_ns status
  start_ns="$(date +%s%N)"
  case "$id" in
    L2)
      rm -f "$artifact_dir/l2-cases.json"
      set +e
      PARITY_L2_ARTIFACT="$artifact_dir/l2-cases.json" \
        dotnet test "$repo_root/tests/Contoso.Lending.ParityTests"
      status=$?
      set -e
      ;;
    L3)
      rm -f "$artifact_dir/l3-report.json"
      set +e
      dotnet run --project "$repo_root/tools/migrator" -- migrate
      status=$?
      if [ "$status" -eq 0 ]; then
        dotnet run --project "$repo_root/tools/migrator" -- \
          verify --report "$artifact_dir/l3-report.json"
        status=$?
      fi
      set -e
      ;;
    L4)
      rm -f "$artifact_dir/l4-scenarios.json"
      set +e
      PARITY_L4_ARTIFACT="$artifact_dir/l4-scenarios.json" \
        "$repo_root/src/Contoso.Lending.Workflow/scripts/run-demo.sh"
      status=$?
      set -e
      ;;
  esac
  end_ns="$(date +%s%N)"
  status_codes["$id"]="$status"
  durations["$id"]="$(( (end_ns - start_ns) / 1000000 ))"
}

for id in L2 L3 L4; do
  if [ "${selected[$id]:-0}" = 1 ]; then
    run_level "$id"
  fi
done

finished_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
export REPO_ROOT="$repo_root" ARTIFACT_DIR="$artifact_dir"
export DASHBOARD="$dashboard" SNAPSHOT="$snapshot"
export STARTED_AT="$started_at" FINISHED_AT="$finished_at"
export SELECTED_LEVELS="$levels"
status_codes_json="{"
durations_json="{"
for id in L2 L3 L4; do
  if [ "${status_codes[$id]+set}" = set ]; then
    status_codes_json+="\"$id\":${status_codes[$id]},"
    durations_json+="\"$id\":${durations[$id]},"
  fi
done
status_codes_json="${status_codes_json%,}"
durations_json="${durations_json%,}"
status_codes_json="$status_codes_json}"
durations_json="$durations_json}"
export STATUS_CODES_JSON="$status_codes_json"
export DURATIONS_JSON="$durations_json"

python3 - <<'PY'
import json
import os
from pathlib import Path

repo = Path(os.environ["REPO_ROOT"])
artifact_dir = Path(os.environ["ARTIFACT_DIR"])
selected = {x.strip() for x in os.environ["SELECTED_LEVELS"].split(",") if x.strip()}

status_codes = json.loads(os.environ["STATUS_CODES_JSON"])
durations = json.loads(os.environ["DURATIONS_JSON"])

def process_ok(level):
    return status_codes.get(level) == 0

def diagnostic(field, actual):
    return {
        "field": field,
        "expected": "structured artifact",
        "actual": actual,
        "status": "fail",
    }

def read_json(level, filename):
    path = artifact_dir / filename
    if not path.exists():
        return None, f"{level} artifact missing: {path}"
    try:
        return json.loads(path.read_text()), None
    except (OSError, json.JSONDecodeError) as exc:
        return None, f"{level} artifact unreadable: {path}: {exc}"

def l2_error(message):
    return {
        "id": "L2",
        "name": "Service — golden corpus parity",
        "status": "fail",
        "durationMs": durations.get("L2", 0),
        "summary": {"cases": 0, "passed": 0, "failed": 1},
        "groups": [{
            "ruleId": "L2-ERROR",
            "title": "structured artifact diagnostic",
            "goldenFile": "",
            "cases": 0,
            "passed": 0,
            "failed": 1,
            "status": "fail",
            "failures": [{"recordIndex": -1, "input": {}, "expected": {}, "actual": message}],
        }],
    }

def l2():
    data, error = read_json("L2", "l2-cases.json")
    if error:
        return l2_error(error)
    if not isinstance(data, list):
        return l2_error("L2 artifact must be a JSON array")
    groups = {}
    required = ("ruleId", "goldenFile", "recordIndex", "input", "expected", "actual",
                "status", "assertionMessage")
    for index, case in enumerate(data):
        if not isinstance(case, dict):
            return l2_error(f"L2 case {index} is not an object")
        missing = [field for field in required if field not in case]
        if missing:
            return l2_error(f"L2 case {index} missing fields: {', '.join(missing)}")
        if case["status"] not in ("pass", "fail"):
            return l2_error(f"L2 case {index} has invalid status: {case['status']!r}")
        key = (case.get("ruleId", ""), case.get("goldenFile", ""))
        groups.setdefault(key, []).append(case)
    output = []
    for (rule_id, golden_file), cases in sorted(groups.items()):
        failures = []
        for case in cases:
            if case.get("status") != "pass":
                failures.append({
                    "recordIndex": case.get("recordIndex", -1),
                    "input": case.get("input", {}),
                    "expected": case.get("expected", {}),
                    "actual": case.get("actual", {}),
                    "assertionMessage": case.get("assertionMessage"),
                })
        passed = len(cases) - len(failures)
        output.append({
            "ruleId": rule_id,
            "title": rule_id,
            "goldenFile": golden_file,
            "cases": len(cases),
            "passed": passed,
            "failed": len(failures),
            "status": "pass" if not failures else "fail",
            "failures": failures,
        })
    if not output:
        return l2_error("L2 artifact contains no cases")
    cases = sum(x["cases"] for x in output)
    passed = sum(x["passed"] for x in output)
    return {
        "id": "L2",
        "name": "Service — golden corpus parity",
        "status": "pass" if all(x["status"] == "pass" for x in output) else "fail",
        "durationMs": durations.get("L2", 0),
        "summary": {"cases": cases, "passed": passed, "failed": cases - passed},
        "groups": output,
    }

def l3_error(message):
    return {
        "id": "L3",
        "name": "Data — Oracle→Postgres verification",
        "status": "fail",
        "durationMs": durations.get("L3", 0),
        "summary": {"rows": 1, "passed": 0, "failed": 1},
        "rows": [{
            "table": "diagnostic",
            "metric": "artifact",
            "oracle": "structured artifact",
            "postgres": message,
            "status": "fail",
        }],
    }

def l3():
    data, error = read_json("L3", "l3-report.json")
    if error:
        return l3_error(error)
    if not isinstance(data, dict) or not isinstance(data.get("rows"), list):
        return l3_error("L3 artifact must be an object with a rows array")
    rows = data["rows"]
    if not rows:
        return l3_error("L3 artifact contains no rows")
    required = ("table", "metric", "oracle", "postgres", "status")
    for index, row in enumerate(rows):
        if not isinstance(row, dict):
            return l3_error(f"L3 row {index} is not an object")
        missing = [field for field in required if field not in row]
        if missing:
            return l3_error(f"L3 row {index} missing fields: {', '.join(missing)}")
        if row["status"] not in ("pass", "fail"):
            return l3_error(f"L3 row {index} has invalid status: {row['status']!r}")
    passed = sum(row.get("status") == "pass" for row in rows)
    return {
        "id": "L3",
        "name": "Data — Oracle→Postgres verification",
        "status": "pass" if process_ok("L3") and passed == len(rows) else "fail",
        "durationMs": durations.get("L3", 0),
        "summary": {
            "rows": len(rows),
            "passed": passed,
            "failed": len(rows) - passed,
        },
        "rows": rows,
    }

def l4_error(message):
    return {
        "id": "L4",
        "name": "End-to-end workflow scenarios",
        "status": "fail",
        "durationMs": durations.get("L4", 0),
        "summary": {"scenarios": 1, "passed": 0, "failed": 1},
        "scenarios": [{
            "name": "diagnostic",
            "workflowId": "",
            "status": "fail",
            "assertions": [diagnostic("artifact", message)],
        }],
    }

def l4():
    data, error = read_json("L4", "l4-scenarios.json")
    if error:
        return l4_error(error)
    if not isinstance(data, list) or not data:
        return l4_error("L4 artifact must be a non-empty JSON array")
    scenarios = []
    required = ("name", "workflowId", "status", "assertions")
    assertion_required = ("field", "expected", "actual", "status")
    for index, scenario in enumerate(data):
        if not isinstance(scenario, dict):
            return l4_error(f"L4 scenario {index} is not an object")
        missing = [field for field in required if field not in scenario]
        if missing:
            return l4_error(f"L4 scenario {index} missing fields: {', '.join(missing)}")
        assertions = scenario.get("assertions", [])
        if not isinstance(assertions, list):
            return l4_error(f"L4 scenario {index} assertions is not an array")
        for assertion_index, assertion in enumerate(assertions):
            if not isinstance(assertion, dict):
                return l4_error(f"L4 assertion {index}/{assertion_index} is not an object")
            missing = [field for field in assertion_required if field not in assertion]
            if missing:
                return l4_error(
                    f"L4 assertion {index}/{assertion_index} missing fields: {', '.join(missing)}")
            if assertion["status"] not in ("pass", "fail"):
                return l4_error(
                    f"L4 assertion {index}/{assertion_index} has invalid status: "
                    f"{assertion['status']!r}")
        scenario_status = "pass" if scenario.get("status") == "pass" and all(
            x.get("status") == "pass" for x in assertions
        ) else "fail"
        scenarios.append({
            "name": scenario.get("name", ""),
            "workflowId": scenario.get("workflowId", ""),
            "status": scenario_status,
            "assertions": assertions,
        })
    passed = sum(x["status"] == "pass" for x in scenarios)
    return {
        "id": "L4",
        "name": "End-to-end workflow scenarios",
        "status": "pass" if process_ok("L4") and passed == len(scenarios) else "fail",
        "durationMs": durations.get("L4", 0),
        "summary": {
            "scenarios": len(scenarios),
            "passed": passed,
            "failed": len(scenarios) - passed,
        },
        "scenarios": scenarios,
    }

builders = {"L2": l2, "L3": l3, "L4": l4}
levels = [builders[level]() for level in ("L2", "L3", "L4") if level in selected]
dashboard = {
    "specVersion": "1.0",
    "startedAt": os.environ["STARTED_AT"],
    "finishedAt": os.environ["FINISHED_AT"],
    "ok": bool(levels) and all(level["status"] == "pass" for level in levels),
    "levels": levels,
}
Path(os.environ["DASHBOARD"]).write_text(json.dumps(dashboard, indent=2) + "\n")
lines = [
    "# Parity dashboard snapshot",
    "",
    f"- Overall: **{'PASS' if dashboard['ok'] else 'FAIL'}**",
    f"- Started: `{dashboard['startedAt']}`",
    f"- Finished: `{dashboard['finishedAt']}`",
    "",
    "| Level | Status | Summary |",
    "| --- | --- | --- |",
]
for level in levels:
    summary = ", ".join(f"{key}={value}" for key, value in level["summary"].items())
    lines.append(f"| {level['id']} | **{level['status'].upper()}** | {summary} |")
Path(os.environ["SNAPSHOT"]).write_text("\n".join(lines) + "\n")
print(json.dumps(dashboard, indent=2))
raise SystemExit(0 if dashboard["ok"] else 1)
PY
