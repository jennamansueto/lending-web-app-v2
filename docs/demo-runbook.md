# Demo runbook — legacy lending desk to four-layer web application

A 15-minute demo script. Four acts: the legacy application, the documentation that
was derived from it, the migrated slice, and the parity red/green moment.

## 0. Prerequisites

| Component | Command | Port |
| --- | --- | --- |
| Oracle (legacy system of record) | `docker start lending-oracle` (legacy repo: `docker-compose.yml` + `database/setup-db.sh`) | 1521 |
| Postgres 16 (target system of record) | `docker compose up -d --wait` | 5432 |
| Service layer (.NET 8) | `ConnectionStrings__Lending='Host=localhost;Port=5432;Database=lending;Username=lending;Password=lending_pw_2014' dotnet run --project src/Contoso.Lending.Api` | 5080 |
| Temporal dev server | `docker compose -f src/Contoso.Lending.Workflow/docker-compose.workflow.yml up -d --wait` | 7233 (UI 8233) |
| Workflow worker | `dotnet run --project src/Contoso.Lending.Workflow -- worker` | — |
| Angular shell + remotes | `npx ng serve shell` and `npx ng serve <remote>` in `ui/` (Node 24) | 4200, 4201–4204 |
| Parity dashboard | `dotnet run --project tools/parity-dashboard` | 5090 |

Node 24 is required by the Angular workspace. Database credentials here are local
demo credentials committed on purpose; there are no real secrets in this repo.

## Act 1 — the legacy application (5 min)

In the legacy repo (`jennamansueto/lending-desktop-app`), run the WinForms client
against Oracle and walk the five screens: loan application, pricing, borrower
lookup, statements, and the launcher. Points to make while clicking:

- Every business rule lives in either a form's code-behind or a PL/SQL package —
  `PKG_LENDING.CALC_LATE_FEE` and `GET_PAYOFF_AMOUNT` are business logic inside
  the database.
- The pricing screen's late-fee box reuses the loan amount as the payment amount
  (ticket LEND-5102). It is a bug. The migration preserves it.
- Borrower search uppercases names but compares tax IDs case-sensitively, an empty
  search returns every row, and `%`/`_` behave as SQL wildcards (LEND-3987).

## Act 2 — the derived documentation (2 min)

Show, in the legacy repo, that the migration started from machine-checkable facts,
not from reading code by eye:

- `docs/business-rules.md` — 55 rules with stable IDs, source file/line, boundary
  semantics and rounding mode.
- `docs/dependency-graph.md` — component graph, screen-to-table matrix, stored-code
  call graph, and the cut lines into the four target layers.
- `docs/data-profile.md` + `database/checks/` — every column, constraint, aggregate
  and row-hash chain, reproducible from a clean seed.
- `parity/golden/` — 688 golden records produced by executing the legacy code and
  the Oracle stored functions. No expected value was derived by hand.

## Act 3 — the migrated slice (4 min)

Open the shell at http://localhost:4200 and run the same inputs as Act 1:

| Screen | Input | Expected output |
| --- | --- | --- |
| Loan application | TERM, $100,000, 60 months, income $600,000, debt $5,000, score 700, collateral $200,000 | `APPROVED FOR UNDERWRITING`, DTI `0.139`, LTV `0.500`, payment `$1,956.61` |
| Loan application | same with score 500 | `DECLINED` / `Credit score 500 below product minimum of 660.` |
| Pricing | TERM, $100,000, 60 months, score 680, LTV 0.5, deposits $0 | rate `7.20 %`, fee `$1,000.00`, payment `$1,989.57` |
| Pricing | late fee, 15 days late | `$150.00` — the LEND-5102 quirk, preserved |
| Borrower lookup | search `a` | rows plus `Pre-qualified products: TERM, LOC, EQUIP` |
| Statements | loan 1 | 84 schedule rows, payoff quote, CSV export |

Architecture points: each screen is an independently buildable micro-frontend
loaded by the shell over native federation; the UI holds no thresholds or
calculations; the Temporal workflow only sequences activity calls and carries
state, which `tests/Contoso.Lending.WorkflowTests` enforces by inspecting the
workflow assembly's references rather than by convention.

## Act 4 — the parity moment (4 min)

1. Open the dashboard at http://localhost:5090 and press **Run parity checks**.
   L2 (688 golden records), L3 (Oracle-to-Postgres row counts, aggregates and hash
   chains) and L4 (approved and declined workflow runs) stream to green.
2. Break one rule: change a risk-spread band in
   `src/Contoso.Lending.Domain/PricingQuoteEngine.cs`.
3. Press **Run parity checks** again. The affected rule group turns red and expands
   to the exact failing golden record — file, record index, input, expected, actual.
4. Revert the change and re-run. Green again.

Closing point: parity is asserted with exact equality and no tolerances, so the
legacy quirks shown in Act 1 are what keeps the suite green — "fixing" one of them
turns the dashboard red.

## One-command equivalent

```bash
PARITY_LEVELS=L2,L3,L4 ./parity/run-all.sh   # writes parity/parity-dashboard.json + .md
```
