#!/usr/bin/env bash
# Layer 3 parity gate: a fresh migration of the legacy Oracle schema into Postgres followed by the
# checksum verifier.
#
# `migrate` truncates the target tables and re-copies every row from Oracle, so this always starts
# from a clean state; `verify` then compares row counts, numeric sums and the two checksums per
# table with zero tolerance and writes parity/reports/data-parity.json.
#
# Oracle is the legacy source (lending-desktop-app, branch phase1-integration) and must already be
# running: docker compose up -d && ./database/setup-db.sh in that repo.
set -uo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
reports_dir="$repo_root/parity/reports"
report="$reports_dir/data-parity.json"
psql_docker=(docker exec -i lending-postgres psql -U lending -d lending -v ON_ERROR_STOP=1)

mkdir -p "$reports_dir"
rm -f "$report"

wait_for() { # wait_for <label> <seconds> <command...>
    local label="$1" deadline=$(( SECONDS + $2 )); shift 2
    until "$@" >/dev/null 2>&1; do
        if [ $SECONDS -ge $deadline ]; then
            echo "L3: timed out waiting for $label"
            return 1
        fi
        sleep 1
    done
    echo "L3: $label ready"
}

echo "L3: starting postgres"
docker compose -f "$repo_root/docker-compose.yml" up -d postgres || exit 1
wait_for "postgres" 120 docker exec lending-postgres pg_isready -U lending -d lending || exit 1

if ! "${psql_docker[@]}" -tAc "select to_regclass('public.borrower')" | grep -q borrower; then
    echo "L3: applying database/postgres/schema.sql"
    "${psql_docker[@]}" -f /database/postgres/schema.sql >/dev/null || exit 1
fi

echo "L3: migrating oracle -> postgres"
dotnet run --project "$repo_root/tools/migrator" -c Debug -- migrate || exit 1

echo "L3: verifying checksums"
dotnet run --project "$repo_root/tools/migrator" -c Debug -- verify
verify_exit=$?

if [ $verify_exit -ne 0 ] || [ ! -f "$report" ]; then
    echo "L3 PARITY FAILED"
    exit 1
fi
echo "report: $report"
echo "L3 PARITY GREEN — zero tolerance, every checksum matched"
