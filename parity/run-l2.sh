#!/usr/bin/env bash
# Layer 2 parity gate: runs the golden-corpus suite and reports per-rule results to
# parity/reports/l2-service.json plus a console summary.
# Exits non-zero if any golden record differs from the legacy output.
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
results_dir="$repo_root/parity/reports"
trx_dir="$results_dir/trx"
report="$results_dir/l2-service.json"

rm -rf "$trx_dir"
mkdir -p "$trx_dir"

dotnet test "$repo_root/tests/Contoso.Lending.ParityTests/Contoso.Lending.ParityTests.csproj" \
    --nologo \
    --logger "trx;LogFileName=parity.trx" \
    --results-directory "$trx_dir"
test_exit=$?

TRX="$trx_dir/parity.trx" REPORT="$report" python3 - <<'PY'
import json, os, re, sys
import xml.etree.ElementTree as ET

trx_path = os.environ["TRX"]
report_path = os.environ["REPORT"]
if not os.path.exists(trx_path):
    print("PARITY: no test results produced at %s" % trx_path)
    sys.exit(1)

ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
root = ET.parse(trx_path).getroot()

record = re.compile(r'ruleId:\s*"(?P<rule>[^"]+)",\s*index:\s*(?P<index>\d+)')
rules = {}
other = {"total": 0, "passed": 0, "failed": 0, "failing": []}

for result in root.iterfind(".//t:UnitTestResult", ns):
    name = result.get("testName") or ""
    outcome = result.get("outcome") or ""
    passed = outcome == "Passed"
    match = record.search(name)
    if match:
        rule = match.group("rule")
        bucket = rules.setdefault(rule, {"total": 0, "passed": 0, "failed": 0, "failing": []})
        identifier = "%s#%s" % (rule, match.group("index"))
    else:
        bucket = other
        identifier = name
    bucket["total"] += 1
    if passed:
        bucket["passed"] += 1
    else:
        bucket["failed"] += 1
        bucket["failing"].append(identifier)

golden_total = sum(b["total"] for b in rules.values())
golden_failed = sum(b["failed"] for b in rules.values())
total = golden_total + other["total"]
failed = golden_failed + other["failed"]

report = {
    "layer": "l2-service",
    "goldenRecords": {"total": golden_total, "passed": golden_total - golden_failed, "failed": golden_failed},
    "supportingTests": other,
    "rules": {rule: rules[rule] for rule in sorted(rules)},
    "totals": {"total": total, "passed": total - failed, "failed": failed},
}
os.makedirs(os.path.dirname(report_path), exist_ok=True)
with open(report_path, "w") as handle:
    json.dump(report, handle, indent=2, sort_keys=False)
    handle.write("\n")

print("")
print("Layer 2 parity — golden corpus")
print("%-14s %7s %7s %7s" % ("RULE", "TOTAL", "PASS", "FAIL"))
for rule in sorted(rules):
    bucket = rules[rule]
    print("%-14s %7d %7d %7d" % (rule, bucket["total"], bucket["passed"], bucket["failed"]))
print("%-14s %7d %7d %7d" % ("GOLDEN TOTAL", golden_total, golden_total - golden_failed, golden_failed))
print("%-14s %7d %7d %7d" % ("supporting", other["total"], other["passed"], other["failed"]))
print("%-14s %7d %7d %7d" % ("ALL TESTS", total, total - failed, failed))
print("report: %s" % report_path)
for rule in sorted(rules):
    for identifier in rules[rule]["failing"]:
        print("FAIL %s" % identifier)
for identifier in other["failing"]:
    print("FAIL %s" % identifier)
print("")
sys.exit(1 if failed else 0)
PY
summary_exit=$?

if [ $test_exit -ne 0 ] || [ $summary_exit -ne 0 ]; then
    echo "PARITY FAILED"
    exit 1
fi
echo "PARITY GREEN — zero tolerance, every golden record matched"
