# Contoso.Lending.Migrator

Oracle → Postgres data-layer CLI for the LENDING schema. Two commands, no others:

| Command | Behaviour |
|---|---|
| `migrate` | Truncates the five Postgres tables, copies every Oracle row (binary `COPY`, PK order), then positions each sequence at its legacy Oracle `LAST_NUMBER`. Re-runnable: two consecutive runs leave an identical database. |
| `verify` | Compares Oracle and Postgres exactly — row counts, per-numeric-column `SUM`, per-date `MIN`/`MAX`, and the spec_version 1.0 row-hash chains — and compares the Postgres side against the committed `database/checks/baseline-oracle.json`. Exits `1` on any mismatch, `0` only when there are none. No tolerances, no epsilons. |

The Postgres-side checksums are produced by executing the committed harness
`database/checks/checksum_postgres.sql` verbatim, so both sides are computed by the
Phase-1 specification rather than by ad-hoc SQL.

Connection strings (defaults are the local docker-compose credentials):

| Variable | Default |
|---|---|
| `MIGRATOR_ORACLE` | `User Id=lending;Password=lending_pw_2014;Data Source=localhost:1521/FREEPDB1` |
| `MIGRATOR_POSTGRES` | `Host=localhost;Port=5432;Database=lending;Username=lending;Password=lending_pw_2014` |
| `MIGRATOR_REPO_ROOT` | auto-detected (nearest ancestor containing `database/checks`) |

```bash
dotnet build tools/migrator
dotnet run --project tools/migrator -- migrate
dotnet run --project tools/migrator -- verify
```

This tool contains no business rules: it copies stored values and never recomputes
them (late fees, payoff amounts, schedules and pricing all live in the service layer).

See [`docs/data-layer.md`](../../docs/data-layer.md) for the schema mapping, sequence
handling, hazard log and full reproduction commands.
