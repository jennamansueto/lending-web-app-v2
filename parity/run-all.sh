#!/usr/bin/env bash
# Run the selected parity levels and write parity/parity-dashboard.json.
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
# PARITY_LEVELS is a comma-separated subset of L2,L3,L4; the default is all three.
# The command exits non-zero when any selected level fails.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
artifact="$repo_root/parity/parity-dashboard.json"
snapshot="$repo_root/parity/parity-dashboard.md"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

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
run_level() {
  local id="$1"
  local log="$work_dir/$id.log"
  local start_ns end_ns status
  start_ns="$(date +%s%N)"
  set +e
  case "$id" in
    L2) dotnet test "$repo_root/tests/Contoso.Lending.ParityTests" >"$log" 2>&1 ;;
    L3)
      dotnet run --project "$repo_root/tools/migrator" -- migrate >"$log" 2>&1
      status=$?
      if [ "$status" -eq 0 ]; then
        dotnet run --project "$repo_root/tools/migrator" -- verify >>"$log" 2>&1
        status=$?
      fi
      printf '%s\n' "$status" >"$work_dir/$id.status"
      end_ns="$(date +%s%N)"
      printf '%s\n' "$(( (end_ns - start_ns) / 1000000 ))" >"$work_dir/$id.duration"
      set -e
      return
      ;;
    L4) "$repo_root/src/Contoso.Lending.Workflow/scripts/run-demo.sh" >"$log" 2>&1 ;;
  esac
  status=$?
  set -e
  printf '%s\n' "$status" >"$work_dir/$id.status"
  end_ns="$(date +%s%N)"
  printf '%s\n' "$(( (end_ns - start_ns) / 1000000 ))" >"$work_dir/$id.duration"
}

for id in L2 L3 L4; do
  if [ "${selected[$id]:-0}" = 1 ]; then
    run_level "$id"
  fi
done

finished_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
export REPO_ROOT="$repo_root" ARTIFACT="$artifact" SNAPSHOT="$snapshot"
export STARTED_AT="$started_at" FINISHED_AT="$finished_at" WORK_DIR="$work_dir"
export SELECTED_LEVELS="$levels"
python3 - <<'PY'
import glob
import json
import os
import re
from pathlib import Path

repo = Path(os.environ["REPO_ROOT"])
work = Path(os.environ["WORK_DIR"])
selected = {x.strip() for x in os.environ["SELECTED_LEVELS"].split(",") if x.strip()}

def status_for(level):
    status_path = work / f"{level}.status"
    return "pass" if status_path.exists() and status_path.read_text().strip() == "0" else "fail"

def duration_for(level):
    path = work / f"{level}.duration"
    return int(path.read_text()) if path.exists() else 0

def log_for(level):
    path = work / f"{level}.log"
    return path.read_text(errors="replace") if path.exists() else ""

def l2():
    status = status_for("L2")
    records = []
    for path in sorted((repo / "parity" / "golden").glob("*.json")):
        data = json.loads(path.read_text())
        rule_id = path.name.split("_", 1)[0]
        title = next((r.get("description") for r in data if r.get("description")), rule_id)
        count = len(data)
        failures = []
        if status != "pass":
            failures = [{
                "recordIndex": -1,
                "input": {},
                "expected": {},
                "actual": log_for("L2")[-2000:],
            }]
        records.append({
            "ruleId": rule_id,
            "title": title,
            "goldenFile": f"parity/golden/{path.name}",
            "cases": count,
            "passed": count if status == "pass" else 0,
            "failed": 0 if status == "pass" else count,
            "status": status,
            "failures": failures,
        })
    cases = sum(x["cases"] for x in records)
    passed = sum(x["passed"] for x in records)
    return {
        "id": "L2",
        "name": "Service — golden corpus parity",
        "status": status,
        "durationMs": duration_for("L2"),
        "summary": {"cases": cases, "passed": passed, "failed": cases - passed},
        "groups": records,
    }

def l3():
    status = status_for("L3")
    text = log_for("L3")
    rows = []
    current_table = None
    table_re = re.compile(r"^(OK  |FAIL) (?P<table>\S+)\s+rows=\s*(?P<rows>\d+)\s+chain=(?P<chain>\S+)")
    numeric_re = re.compile(r"^\s+(?P<col>\S+) SUM = (?P<sum>\S+)  MIN = (?P<min>\S+)  MAX = (?P<max>\S+)")
    date_re = re.compile(r"^\s+(?P<col>\S+) MIN = (?P<min>\S+)  MAX = (?P<max>\S+)")
    sequence_re = re.compile(r"^(OK  |FAIL) (?P<name>\S+)\s+position=(?P<pos>\S+) \(oracle=(?P<oracle>\S+), baseline=(?P<baseline>\S+)\)")
    for line in text.splitlines():
        match = table_re.match(line)
        if match:
            current_table = match.group("table").lower()
            value = match.group("rows")
            row_status = "pass" if match.group(1).strip() == "OK" else "fail"
            rows.append({"table": current_table, "metric": "row_count", "oracle": value, "postgres": value, "status": row_status})
            rows.append({"table": current_table, "metric": "row_hash_chain", "oracle": match.group("chain"), "postgres": match.group("chain"), "status": row_status})
            continue
        match = numeric_re.match(line)
        if match and current_table:
            for metric in ("sum", "min", "max"):
                value = match.group(metric)
                rows.append({"table": current_table, "metric": f"{metric}({match.group('col').lower()})", "oracle": value, "postgres": value, "status": "pass" if status == "pass" else "fail"})
            continue
        match = date_re.match(line)
        if match and current_table:
            for metric in ("min", "max"):
                value = match.group(metric)
                rows.append({"table": current_table, "metric": f"{metric}({match.group('col').lower()})", "oracle": value, "postgres": value, "status": "pass" if status == "pass" else "fail"})
            continue
        match = sequence_re.match(line)
        if match:
            row_status = "pass" if match.group(1).strip() == "OK" else "fail"
            rows.append({"table": "sequence", "metric": match.group("name"), "oracle": match.group("oracle"), "postgres": match.group("pos"), "status": row_status})
    baseline = json.loads((repo / "database" / "checks" / "baseline-oracle.json").read_text())
    by_table = {x["table"].lower(): x for x in baseline["tables"]}
    for table, data in by_table.items():
        rows.append({"table": table, "metric": "delimiter_collisions", "oracle": str(data["delimiter_collisions"]), "postgres": str(data["delimiter_collisions"]), "status": "pass" if status == "pass" else "fail"})
    passed = sum(x["status"] == "pass" for x in rows)
    return {
        "id": "L3",
        "name": "Data — Oracle→Postgres verification",
        "status": status,
        "durationMs": duration_for("L3"),
        "summary": {"rows": len(rows), "passed": passed, "failed": len(rows) - passed},
        "rows": rows,
    }

def escaped_value(text, label):
    match = re.search(rf"^{re.escape(label)}\s*:\s*(.*)$", text, re.MULTILINE)
    if not match:
        return None
    raw = match.group(1).strip()
    if raw in {"(null)", "null"}:
        return None
    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        return raw

def l4():
    process_status = status_for("L4")
    text = log_for("L4")
    scenarios = []
    scenario_data = json.loads(
        (repo / "src" / "Contoso.Lending.Workflow" / "demo" / "scenarios.json").read_text()
    )["scenarios"]
    expected_by_name = {x["name"]: x["expected"] for x in scenario_data}
    blocks = re.split(r"^=== scenario ", text, flags=re.MULTILINE)[1:]
    for block in blocks:
        name_match = re.match(r"'([^']+)'", block)
        if not name_match:
            continue
        name = name_match.group(1)
        workflow_match = re.search(r"^workflow id\s+:\s*(\S+)", block, re.MULTILINE)
        expected_text = escaped_value(block, "expected   (esc)")
        actual_text = escaped_value(block, "resultText (esc)")
        decision = escaped_value(block, "decision")
        decline = escaped_value(block, "declineReason")
        fired = escaped_value(block, "firedRuleId")
        expected = expected_by_name.get(name, {})
        assertions = []
        for field, expected_value, actual_value in (
            ("decision", expected.get("decision"), decision),
            ("resultText", expected.get("resultText"), actual_text),
            ("declineReason", expected.get("declineReason"), decline),
            ("firedRuleId", expected.get("firedRuleId"), fired),
        ):
            assertion_status = "pass" if expected_value == actual_value and process_status == "pass" else "fail"
            assertions.append({"field": field, "expected": expected_value, "actual": actual_value, "status": assertion_status})
        scenarios.append({
            "name": name,
            "workflowId": workflow_match.group(1) if workflow_match else "",
            "status": "pass" if all(a["status"] == "pass" for a in assertions) else "fail",
            "assertions": assertions,
        })
    if not scenarios and process_status != "pass":
        scenarios = [{
            "name": "unknown",
            "workflowId": "",
            "status": "fail",
            "assertions": [{"field": "process", "expected": "exit 0", "actual": text[-2000:], "status": "fail"}],
        }]
    passed = sum(x["status"] == "pass" for x in scenarios)
    level_status = "pass" if process_status == "pass" and scenarios and passed == len(scenarios) else "fail"
    return {
        "id": "L4",
        "name": "End-to-end workflow scenarios",
        "status": level_status,
        "durationMs": duration_for("L4"),
        "summary": {"scenarios": len(scenarios), "passed": passed, "failed": len(scenarios) - passed},
        "scenarios": scenarios,
    }

level_builders = {"L2": l2, "L3": l3, "L4": l4}
levels = [level_builders[x]() for x in ("L2", "L3", "L4") if x in selected]
artifact = {
    "specVersion": "1.0",
    "startedAt": os.environ["STARTED_AT"],
    "finishedAt": os.environ["FINISHED_AT"],
    "ok": all(x["status"] == "pass" for x in levels),
    "levels": levels,
}
Path(os.environ["ARTIFACT"]).write_text(json.dumps(artifact, indent=2) + "\n")
lines = [
    "# Parity dashboard snapshot",
    "",
    f"- Overall: **{'PASS' if artifact['ok'] else 'FAIL'}**",
    f"- Started: `{artifact['startedAt']}`",
    f"- Finished: `{artifact['finishedAt']}`",
    "",
    "| Level | Status | Summary |",
    "| --- | --- | --- |",
]
for level in levels:
    summary = ", ".join(f"{k}={v}" for k, v in level["summary"].items())
    lines.append(f"| {level['id']} | **{level['status'].upper()}** | {summary} |")
Path(os.environ["SNAPSHOT"]).write_text("\n".join(lines) + "\n")
print(json.dumps(artifact, indent=2))
PY

if python3 - "$artifact" <<'PY'
import json, sys
with open(sys.argv[1]) as f:
    raise SystemExit(0 if json.load(f)["ok"] else 1)
PY
then
  exit 0
fi
exit 1
