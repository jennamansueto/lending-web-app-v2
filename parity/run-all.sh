#!/usr/bin/env bash
# Runs every parity level and aggregates the result.
#
#   L1  UI       parity/run-l1.sh   Angular screens in a browser vs the golden corpus
#   L2  service  parity/run-l2.sh   772 golden records vs the domain
#   L3  data     parity/run-l3.sh   fresh Oracle -> Postgres migrate + checksum verify
#   L4  e2e      parity/run-l4.sh   Temporal workflow scenarios
#
# Execution order is L2, L3, L1, L4: L3 re-migrates the database from Oracle, so it runs before
# the levels that read it, and L4 books loans into it, so it runs last.
#
# Writes parity/reports/parity-dashboard.json (aggregate, machine readable) and
# parity/reports/parity-dashboard.md (snapshot). Exits non-zero if any level is red.
#
# PARITY_LEVELS limits the run, e.g. PARITY_LEVELS="l2" bash parity/run-all.sh
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
reports_dir="$repo_root/parity/reports"
levels="${PARITY_LEVELS:-l2 l3 l1 l4}"

mkdir -p "$reports_dir"
rm -f "$reports_dir"/run-all-*.log "$reports_dir/parity-dashboard.json" "$reports_dir/parity-dashboard.md"

status_file="$reports_dir/run-all-status.txt"
: >"$status_file"

# ##PARITY lines are the machine-readable progress markers the live dashboard subscribes to.
echo "##PARITY run status=running levels=$levels"

overall=0
for level in $levels; do
    log="$reports_dir/run-all-$level.log"
    echo ""
    echo "##PARITY level=$level status=running"
    echo "=============================================================================="
    echo "parity $level — bash parity/run-$level.sh"
    echo "=============================================================================="
    started=$(date -u +%s)
    bash "$repo_root/parity/run-$level.sh" 2>&1 | tee "$log"
    level_exit=${PIPESTATUS[0]}
    finished=$(date -u +%s)
    if [ "$level_exit" -ne 0 ]; then
        overall=1
        echo "$level fail $started $finished" >>"$status_file"
        echo "##PARITY level=$level status=fail"
    else
        echo "$level pass $started $finished" >>"$status_file"
        echo "##PARITY level=$level status=pass"
    fi
done

REPO_ROOT="$repo_root" STATUS_FILE="$status_file" python3 - <<'PY'
import json, os, sys, time

repo_root = os.environ["REPO_ROOT"]
reports_dir = os.path.join(repo_root, "parity", "reports")

DEFINITIONS = {
    "l1": {
        "name": "Layer 1 — UI",
        "description": "Angular shell and remotes driven in a browser, every rendered value compared to the golden corpus, the service and the system of record",
        "script": "parity/run-l1.sh",
        "report": "parity/reports/l1-ui.json",
        "unit": "checks",
    },
    "l2": {
        "name": "Layer 2 — Service",
        "description": "Golden corpus replayed against the domain, grouped by business rule",
        "script": "parity/run-l2.sh",
        "report": "parity/reports/l2-service.json",
        "unit": "tests",
    },
    "l3": {
        "name": "Layer 3 — Data",
        "description": "Fresh Oracle to Postgres migration, then row counts, numeric sums and table checksums",
        "script": "parity/run-l3.sh",
        "report": "parity/reports/data-parity.json",
        "unit": "checksums",
    },
    "l4": {
        "name": "Layer 4 — Workflow",
        "description": "Temporal origination workflow scenarios run end to end against the live stack",
        "script": "parity/run-l4.sh",
        "report": "parity/reports/l4-e2e.json",
        "unit": "scenarios",
    },
}


def load(path):
    try:
        with open(path) as handle:
            return json.load(handle)
    except (OSError, ValueError):
        return None


def totals_of(level, report):
    if report is None:
        return {"total": 0, "passed": 0, "failed": 0}
    if level == "l3":
        return {
            "total": report["checks_total"],
            "passed": report["checks_total"] - report["checks_failed"],
            "failed": report["checks_failed"],
        }
    return report["totals"]


levels = []
for line in open(os.environ["STATUS_FILE"]):
    level, status, started, finished = line.split()
    definition = DEFINITIONS[level]
    report_path = os.path.join(repo_root, definition["report"])
    report = load(report_path)
    totals = totals_of(level, report)
    if report is None:
        status = "fail"
    levels.append(
        {
            "id": level,
            "name": definition["name"],
            "description": definition["description"],
            "script": definition["script"],
            "report": definition["report"],
            "unit": definition["unit"],
            "status": status,
            "totals": totals,
            "durationSeconds": int(finished) - int(started),
            "log": "parity/reports/run-all-%s.log" % level,
        }
    )

levels.sort(key=lambda entry: entry["id"])
failed_levels = [entry["id"] for entry in levels if entry["status"] != "pass"]
dashboard = {
    "generatedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
    "tolerance": "zero",
    "status": "fail" if failed_levels else "pass",
    "levels": levels,
    "totals": {
        "total": sum(entry["totals"]["total"] for entry in levels),
        "passed": sum(entry["totals"]["passed"] for entry in levels),
        "failed": sum(entry["totals"]["failed"] for entry in levels),
    },
}

json_path = os.path.join(reports_dir, "parity-dashboard.json")
with open(json_path, "w") as handle:
    json.dump(dashboard, handle, indent=2)
    handle.write("\n")

lines = [
    "# Parity dashboard",
    "",
    "Generated by `bash parity/run-all.sh` at %s. Comparisons are exact: tolerance is zero at every"
    % dashboard["generatedAt"],
    "level.",
    "",
    "**Overall: %s**" % ("PASS" if dashboard["status"] == "pass" else "FAIL"),
    "",
    "| Level | Status | Passed | Failed | Total | Duration | Report |",
    "| --- | --- | ---: | ---: | ---: | ---: | --- |",
]
for entry in levels:
    lines.append(
        "| %s | %s | %d | %d | %d %s | %ds | `%s` |"
        % (
            entry["name"],
            "PASS" if entry["status"] == "pass" else "FAIL",
            entry["totals"]["passed"],
            entry["totals"]["failed"],
            entry["totals"]["total"],
            entry["unit"],
            entry["durationSeconds"],
            entry["report"],
        )
    )
lines += [
    "| **All levels** | **%s** | **%d** | **%d** | **%d** | | `parity/reports/parity-dashboard.json` |"
    % (
        "PASS" if dashboard["status"] == "pass" else "FAIL",
        dashboard["totals"]["passed"],
        dashboard["totals"]["failed"],
        dashboard["totals"]["total"],
    ),
    "",
]
for entry in levels:
    lines += [
        "## %s" % entry["name"],
        "",
        entry["description"] + ".",
        "",
        "```",
        "$ bash %s" % entry["script"],
        "%s: %d/%d %s passed"
        % (
            entry["id"].upper(),
            entry["totals"]["passed"],
            entry["totals"]["total"],
            entry["unit"],
        ),
        "```",
        "",
    ]

with open(os.path.join(reports_dir, "parity-dashboard.md"), "w") as handle:
    handle.write("\n".join(lines).rstrip() + "\n")

print("")
print("Parity dashboard — all levels")
print("%-22s %-6s %7s %7s %7s" % ("LEVEL", "STATUS", "TOTAL", "PASS", "FAIL"))
for entry in levels:
    print(
        "%-22s %-6s %7d %7d %7d"
        % (
            entry["name"],
            "PASS" if entry["status"] == "pass" else "FAIL",
            entry["totals"]["total"],
            entry["totals"]["passed"],
            entry["totals"]["failed"],
        )
    )
print(
    "%-22s %-6s %7d %7d %7d"
    % (
        "ALL LEVELS",
        "PASS" if dashboard["status"] == "pass" else "FAIL",
        dashboard["totals"]["total"],
        dashboard["totals"]["passed"],
        dashboard["totals"]["failed"],
    )
)
print("snapshot: %s" % os.path.join(reports_dir, "parity-dashboard.md"))
print("aggregate: %s" % json_path)
print("")
sys.exit(1 if failed_levels else 0)
PY
aggregate_exit=$?

rm -f "$status_file"

if [ $overall -ne 0 ] || [ $aggregate_exit -ne 0 ]; then
    echo "##PARITY run status=fail"
    echo "PARITY FAILED — at least one level is red"
    exit 1
fi
echo "##PARITY run status=pass"
echo "PARITY GREEN — all levels matched the legacy system with zero tolerance"
