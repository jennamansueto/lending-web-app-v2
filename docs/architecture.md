# Contoso Commercial Lending — 4-Layer Web Architecture

Modernized replacement for the legacy `lending-desktop-app` (.NET Framework 4.7.2 WinForms fat
client + Oracle with business logic in code-behind and PL/SQL).

The migration is **behavior-preserving**: every legacy business rule, including its rounding
behavior, boundary strictness, user-visible strings and known bugs, is reproduced exactly and
proven by a parity harness against golden records generated from the legacy system.

## Layers

| # | Layer | Contents | Technology | Must NOT contain |
|---|-------|----------|------------|------------------|
| 1 | Data / System of Record | Tables, keys, declarative constraints, migration + checksum tooling | Postgres 16 (stand-in for Lakebase) | Procedural business logic (no PL/pgSQL rule functions) |
| 2 | Service / Business Logic | **All** business rules: eligibility, pre-qualification, pricing, amortization, servicing (late fee, payoff) | .NET 8 (domain library + minimal API) | Orchestration/state machines, presentation formatting decisions beyond the legacy-defined output strings |
| 3 | Workflow / Orchestration | Sequencing and state of the origination process; calls layer 2 via activities | Temporal (.NET SDK) | Any calculation, threshold, or decision logic; any direct DB access |
| 4 | UI / Presentation | One screen per legacy screen, same inputs/labels/result semantics | Angular micro frontends (shell + remotes, Module Federation) | Any calculation or decision logic; any shared business state between remotes |

### Layer boundaries that are enforced by tests, not convention

- **Workflow purity test** — asserts the workflow assembly's referenced assemblies contain no
  domain, data-access or database packages (no `Contoso.Lending.Domain`, no `Npgsql`, no
  `Oracle.*`). The workflow may only reach the service through its activity HTTP client.
- **Service parity test** — loads every golden record in `parity/golden/*.json` and asserts
  exact equality. No epsilon, no tolerance; decline strings compared verbatim.
- **Data parity verify** — row counts, per-column numeric sums and text/date checksums must match
  the Oracle baseline byte-for-byte.

## Where legacy logic lands

| Legacy home | Rule families | Target layer |
|---|---|---|
| `LoanApplicationForm.btnSubmit_Click` code-behind | BR-ELG-001..009 eligibility | Service (domain engine) |
| `BorrowerLookupForm.grid_SelectionChanged` code-behind | BR-PQL-001 pre-qualification hint | Service (domain engine) |
| `Core/LoanCalculator` | BR-PRC-001..006 pricing, BR-AMT-001..002 amortization | Service (domain engine) |
| `PKG_LENDING.CALC_LATE_FEE` / `GET_PAYOFF_AMOUNT` (PL/SQL) | BR-SVC-001..002 servicing | Service (domain engine); the database keeps **no** procedural logic |
| Oracle schema constraints | BR-DAT-* declarative rules | Data (Postgres DDL) |
| WinForms formatting/messages | BR-UI-* presentation strings | Result strings are produced by the **service** (they are behavior under parity test); the UI renders them |

Note on BR-UI-*: user-visible result text such as `DECLINED\n<reason>` and
`APPROVED FOR UNDERWRITING\nDTI: 0.412   LTV: 0.643` is part of the behavioral contract, so it is
produced and parity-tested in the service layer. The UI owns layout, not wording.

## Repository layout

```
database/postgres/            Postgres DDL (schema, constraints, sequences) + reset scripts
tools/migrator/               Oracle -> Postgres migration CLI: `migrate` | `verify`
tools/parity-dashboard/       Live parity dashboard (runs the 4-level parity suite on demand)
src/Contoso.Lending.Domain/   Business rules (pure, no I/O)
src/Contoso.Lending.Api/      Minimal API exposing the domain + data reads
src/Contoso.Lending.Workflow/ Temporal workflow definitions + worker (state only)
ui/shell/                     Angular host application
ui/remotes/loan-application/  Angular remote (legacy "New Loan Application")
ui/remotes/pricing/           Angular remote (legacy "Pricing & Amortization")
ui/remotes/borrower-lookup/   Angular remote (legacy "Borrower Lookup")
ui/remotes/statements/        Angular remote (legacy "Statements Export")
tests/Contoso.Lending.ParityTests/   Golden-corpus parity suite (L2)
tests/Contoso.Lending.ArchTests/     Workflow purity test
parity/golden/                Golden records copied from the legacy repo (behavioral contract)
docs/                         Architecture, API contract, migrated legacy documentation, demo runbook
```

## Parity levels

| Level | What it proves | Evidence |
|---|---|---|
| L1 UI | Every legacy screen exists and produces the same values | Browser recording + screenshots checked against the golden corpus |
| L2 Service | Every business rule matches exactly | Golden-corpus test run (all records, zero tolerance) |
| L3 Data | Migrated data is identical | Fresh migrate + checksum verify report |
| L4 E2E | The whole stack, orchestrated, reaches legacy outcomes | Temporal workflow runs (approved + declined) asserted against legacy expectations |

All four are runnable in one command (`parity/run-all.sh`) and from the parity dashboard UI.

Dashboard entrypoint: `dotnet run --project tools/parity-dashboard` from the repo root; it reads
the structured `parity/parity-dashboard.json` snapshot written by `parity/run-all.sh`.
