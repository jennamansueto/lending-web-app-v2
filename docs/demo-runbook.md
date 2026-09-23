# Demo runbook

A 12–15 minute walkthrough of the modernization: the legacy desk, the Phase 1 documentation
extracted from it, the migrated slice running on the new stack, and the parity dashboard going red
and back to green.

Two repositories are involved:

- legacy — [`jennamansueto/lending-desktop-app`](https://github.com/jennamansueto/lending-desktop-app), branch `phase1-integration`
- target — this repository, branch `phase3-base` (or the branch under review)

## Before the demo

```bash
# legacy Oracle (system of record for the migration)
cd lending-desktop-app
docker compose up -d && ./database/setup-db.sh        # lending/lending_pw_2014@localhost:1521/FREEPDB1

# target stack: Postgres 16 + Temporal
cd ../lending-web-app-v2
docker compose up -d

# one full parity run so the dashboard opens on a green board
bash parity/run-all.sh

# the live dashboard
node tools/parity-dashboard/server.mjs                # http://localhost:5090
```

`parity/run-all.sh` leaves the service API and the Angular dev server running only if they were
already up; for the UI part of the demo start them explicitly:

```bash
dotnet run --project src/Contoso.Lending.Api          # http://localhost:5080
(cd ui && npx nx serve shell)                         # http://localhost:4200
```

## 1. The legacy desk (3 min)

Open the legacy repository and show where the business logic actually lives:

- `src/LendingDesk/Forms/LoanApplicationForm.cs` — eligibility rules in UI code-behind.
- `src/LendingDesk/Forms/BorrowerLookupForm.cs` — the same pre-qualification rule, duplicated.
- `database/plsql/pkg_lending.sql` — `PKG_LENDING.CALC_LATE_FEE` and `GET_PAYOFF_AMOUNT`: more
  business rules, this time inside Oracle.

The point to make: there is no service layer. A rule is a screen, a stored procedure, or both, and
the two copies do not always agree.

## 2. Phase 1 — documentation extracted from the legacy system (3 min)

In the legacy repository:

- `docs/business-rules.md` — every rule found in the code-behind and PL/SQL, with its identifier
  (`BR-ELG-*`, `BR-PRC-*`, `BR-SVC-*`, `BR-AMT-*`, `BR-PQL-*`), including the quirks that are
  bugs but are load-bearing (the $25 late-fee floor charged on a $0.00 payment; the payoff quote
  that sums late fees with no paid flag).
- `docs/data-profile.md` and `docs/dependency-graph.md` — the Oracle schema and what calls what.
- `parity/golden/` — 772 input → output records captured from the running legacy system by
  `tools/ParityCorpusGenerator`. This is the contract the new system has to satisfy; it is copied
  into this repository unchanged and is never edited.

## 3. The migrated slice (4 min)

In the target repository, walk the four layers (see `docs/architecture.md`):

1. **Data** — `database/postgres/schema.sql` and `tools/migrator`: Postgres 16 is the new system of
   record, loaded from Oracle by `migrate` and checked by `verify`.
2. **Service** — `src/Contoso.Lending.Domain`: every rule from Phase 1, one method per rule group,
   with the rule identifier in the comment. `src/Contoso.Lending.Api` exposes it on port 5080.
3. **Workflow** — `src/Contoso.Lending.Workflow`: Temporal sequences the origination steps and
   holds no business logic (a purity test enforces that).
4. **UI** — `ui/`: the Angular shell composes one federated remote per legacy screen. Open
   <http://localhost:4200>, run the same loan through **New loan application** that you ran on the
   WinForms screen, and show the same decision, rate, payment and fee.

## 4. The parity moment (5 min)

Open the dashboard at <http://localhost:5090>. It shows the four levels of the last run and the
`Run parity checks` button.

1. **Green.** Press **Run parity checks**. The levels move pending → running → pass as the run
   streams in, ending at 870/870 checks with tolerance zero.
2. **Break one rule.** In `src/Contoso.Lending.Domain/ServicingEngine.cs`, change the BR-SVC-001
   late-fee cap from `150m` to `175m` — a change that looks harmless and is the kind of "cleanup" a
   rewrite invites.
3. **Red.** Press **Run parity checks** again. Layer 2 turns red: 12 of 780. Expand
   `BR-SVC-001 late fee` and scroll to the failing records — each one shows the input
   (`{"paymentAmount":3563.79,"daysLate":11}`), the expected value from the golden corpus
   (`{"lateFee":150}`), the actual value (`175`), the xunit assertion and a link to
   `parity/golden/BR-SVC-001_late_fee.json`. The other three levels stay green, so the blast radius
   is visible immediately.
4. **Green again.** Revert the cap to `150m`, press **Run parity checks** once more, and watch the
   board return to all-pass.

Recording of exactly this sequence: [`docs/media/dashboard-red-green.webp`](media/dashboard-red-green.webp).
Recording of the Layer 1 browser run: [`docs/media/l1-ui-walkthrough.webp`](media/l1-ui-walkthrough.webp).

## Closing

- `bash parity/run-all.sh` is the gate: one command, four levels, non-zero on any red.
- The committed snapshot `parity/reports/parity-dashboard.md` is what the pipeline produces.
- Nothing is verified by inspection: every number on the dashboard comes from a report written by
  a parity script, compared with exact equality against the legacy system.
