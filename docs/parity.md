# Parity verification

The migration is only finished when the modernized stack produces, byte for byte, what the legacy
WinForms/Oracle desk produced. That is checked at four levels, every one of them with **zero
tolerance** — no epsilons, no rounding slack, no "close enough" comparisons anywhere in the harness.

| Level | Gate | What it compares | Report |
|---|---|---|---|
| L1 UI | `parity/run-l1.sh` | Angular shell + remotes driven in a real browser; every rendered value against the golden corpus, the service and the system of record | `parity/reports/l1-ui.json` |
| L2 service | `parity/run-l2.sh` | 772 golden records replayed against `Contoso.Lending.Domain`, grouped by business rule | `parity/reports/l2-service.json` |
| L3 data | `parity/run-l3.sh` | fresh Oracle → Postgres migration, then row counts, numeric sums and two checksums per table | `parity/reports/data-parity.json` |
| L4 workflow | `parity/run-l4.sh` | `parity/scenarios/*` executed as real Temporal workflows against the live stack | `parity/reports/l4-e2e.json` |

`parity/run-all.sh` runs all four and aggregates them.

## One command

```bash
bash parity/run-all.sh
```

It runs L2, L3, L1, L4 in that order (L3 re-migrates the database, so it precedes the levels that
read it; L4 books loans into it, so it goes last), writes

- `parity/reports/parity-dashboard.json` — the machine-readable aggregate,
- `parity/reports/parity-dashboard.md` — the committed snapshot,
- `parity/reports/run-all-<level>.log` — the full output of each level,

and exits non-zero if any level is red. `PARITY_LEVELS="l2 l4" bash parity/run-all.sh` limits the
run to selected levels.

Current snapshot: [`parity/reports/parity-dashboard.md`](../parity/reports/parity-dashboard.md).

## Prerequisites

| Requirement | Used by | Notes |
|---|---|---|
| .NET 8 SDK | L1, L2, L3, L4 | domain, API, migrator, worker |
| Docker | L3, L4 | `docker compose up -d` brings up Postgres 16 and the Temporal dev server |
| Legacy Oracle | L3 | `jennamansueto/lending-desktop-app` branch `phase1-integration`: `docker compose up -d && ./database/setup-db.sh` (`lending/lending_pw_2014@localhost:1521/FREEPDB1`) |
| Node 20+ | L1, dashboard | installed via nvm on the parity host; `parity/run-l1.sh` sources `$NVM_DIR/nvm.sh` when `node` is not on `PATH` |

The scripts start what they can: `run-l3.sh` starts Postgres and applies
`database/postgres/schema.sql` if the schema is absent; `run-l1.sh` starts the service API (5080)
and the Angular dev server (4200 + remotes on 4201–4204) unless they are already listening, and
installs the browser harness dependencies (`parity/l1/node_modules`, Playwright Chromium) on first
use; `run-l4.sh` starts the API and the origination worker.

## Level 1 — UI

`parity/l1/run-l1.mjs` drives Chromium through every screen of the shell: new loan application,
pricing & amortization, borrower lookup, statements export, plus the shell itself. Each rendered
value is parsed out of the DOM and compared against the golden record, the service response or the
row in the system of record that produced it. Decimal values are canonicalized as decimal strings
(no floating point) and compared exactly. Along with the report the harness writes per-screen
screenshots (`parity/reports/l1-screens/`) and a video of the run (`parity/reports/l1-video/`).

```bash
bash parity/run-l1.sh
```

## Level 2 — Service

`parity/run-l2.sh` runs `tests/Contoso.Lending.ParityTests`, one xunit case per golden record, and
turns the TRX into a per-rule report: for every business rule, the golden file behind it, the pass
and fail counts, and for each failure the record index and the expected/actual values from the
assertion.

```bash
bash parity/run-l2.sh
```

## Level 3 — Data

`parity/run-l3.sh` runs the migrator twice:

```bash
dotnet run --project tools/migrator -c Debug -- migrate   # truncate + re-copy every row from Oracle
dotnet run --project tools/migrator -c Debug -- verify    # row counts, numeric sums, table checksums
```

`migrate` always starts from a clean target, so the level is a fresh migration on every run.
`verify` compares Oracle and Postgres side by side and records both values per check in
`parity/reports/data-parity.json` (`"tolerance": "zero"`).

## Level 4 — Workflow

`parity/run-l4.sh` starts the API and the Temporal origination worker and executes every scenario
in `parity/scenarios/` as a real workflow, comparing the final workflow result — decision, rate,
payment, fees — with the legacy expectations. The report records the workflow id and the booked
loan id per scenario.

## Live dashboard

```bash
node tools/parity-dashboard/server.mjs      # http://localhost:5090  (or: npm start --prefix tools/parity-dashboard)
```

Open the page and press **Run parity checks**. The backend shells out to `parity/run-all.sh`,
follows the `##PARITY` progress markers it prints, and streams state over server-sent events, so
each level moves pending → running → pass/fail as the run proceeds; the console pane mirrors the
script output. When a level finishes, its structured report is loaded and rendered:

- L2 as expandable rows per business rule; expanding lists every golden record with its input and
  expected value, and a failing record shows the actual value, the xunit assertion and a link to
  the exact golden file;
- L3 one row per table check with the Oracle and Postgres values side by side;
- L4 one row per scenario with the asserted outcome, the workflow id and the booked loan;
- L1 one row per screen with each individual check.

The dashboard contains no business logic: it runs the existing scripts and renders the reports they
write. A pass shown on the page is a pass recorded in `parity/reports/`.

To reproduce the red → green demo (breaks the late-fee cap locally, runs, expands the failing rule,
restores the file, runs again, and records the whole thing to
`parity/reports/dashboard-run/`):

```bash
node tools/parity-dashboard/server.mjs &
node tools/parity-dashboard/record-red-green.mjs
```

The script restores `src/Contoso.Lending.Domain/ServicingEngine.cs` on every exit path; nothing
broken is ever committed.
