#!/usr/bin/env bash
# Creates (or recreates) the Postgres system of record in the lending-postgres
# container. Structure only — data arrives via `tools/migrator migrate`.
#
#   ./database/setup-postgres.sh           # apply database/postgres/schema.sql
#   ./database/setup-postgres.sh reset     # apply database/postgres/reset.sql
set -euo pipefail

CONTAINER="${LENDING_POSTGRES_CONTAINER:-lending-postgres}"
DB="${LENDING_POSTGRES_DB:-lending}"
USER_NAME="${LENDING_POSTGRES_USER:-lending}"
SCRIPT="${1:-schema}"

case "$SCRIPT" in
  schema|reset) ;;
  *) echo "usage: $0 [schema|reset]" >&2; exit 2 ;;
esac

echo "Waiting for Postgres to be ready..."
for _ in $(seq 1 60); do
  if docker exec "$CONTAINER" pg_isready -U "$USER_NAME" -d "$DB" >/dev/null 2>&1; then
    break
  fi
  sleep 2
done

docker exec -i "$CONTAINER" psql -v ON_ERROR_STOP=1 -q -U "$USER_NAME" -d "$DB" \
  -f "/database/postgres/${SCRIPT}.sql"

echo "Done. lending schema (${SCRIPT}) applied on localhost:5432/${DB}."
