# Service / business-logic layer

This layer is the single source of truth for every business rule extracted from the
legacy desktop application (`lending-desktop-app`). All rule logic lives in
`src/Contoso.Lending.Domain` (pure .NET 8, no I/O); `src/Contoso.Lending.Data` is
Npgsql data access only (zero rules, zero thresholds, zero calculations); and
`src/Contoso.Lending.Api` exposes the behavior exactly per `docs/api-contract.md`.
Every extracted rule carries its catalog ID plus its legacy `file:line` in a source
comment (e.g. `// BR-PRC-005 LoanCalculator.cs:41`).

## Layout

| Project | Role |
| --- | --- |
| `src/Contoso.Lending.Domain` | All business rules, exact legacy strings, rounding and quirks. Pure functions; no database, no HTTP. |
| `src/Contoso.Lending.Data` | Npgsql repositories against the parallel `database/postgres/schema.sql` (lower_snake_case legacy names). Reads/writes rows; computes nothing. |
| `src/Contoso.Lending.Api` | ASP.NET Core minimal API implementing `docs/api-contract.md` verbatim (field names, `ruleIds`, 400 + `{ "message": ... }` on legacy validation text). |
| `tests/Contoso.Lending.ParityTests` | Golden-corpus parity suite (see below). |
| `parity/golden` | Verbatim copy of the legacy corpus — 24 files, 688 records. Never edited. |

## Numeric and rounding policy

- Money and rates are `decimal` end-to-end and serialize as JSON numbers.
- `MidpointRounding.AwayFromZero` wherever legacy uses it (per-period interest,
  priced rate, origination fee, late fee, payoff accrual).
- Legacy default banker's rounding (`Math.Round(x, 4)`, ToEven) is preserved for
  persisted DTI/LTV (BR-ELG-010, `ApplicationPersistence.Build`).
- The single deliberate `double` round-trip is reproduced bit-for-bit in
  `LoanCalculator.MonthlyPayment`: `(decimal)Math.Pow((double)(1m + i), termMonths)`
  (LEND-4471). It is not "improved".
- All display strings are pinned to en-US via `LegacyCulture` so `C2`/`0.000`/`0.00`
  output is byte-identical on any host (BR-UI-015).

## Preserved legacy bugs and quirks (never fixed)

| Ticket / quirk | Where preserved |
| --- | --- |
| LEND-5102 — pricing screen reuses the Loan Amount box as the late-fee payment amount | `PricingQuoteEngine.LateFeeFromPricingScreen` |
| LEND-3987 — borrower-lookup pre-qual thresholds are an independent manual copy of the eligibility floors | `PrequalificationEngine` (deliberately not derived from `EligibilityEngine`) |
| LEND-4471 — `Math.Pow` double round-trip in the payment formula | `LoanCalculator.MonthlyPayment` |
| Case-sensitive tax-id match beside `UPPER()` name match; `%`/`_` stay live SQL wildcards; empty search returns all rows | `BorrowerSearchSemantics` + `BorrowerRepository` SQL |
| `ACTIVE_LOANS` counts every `LOAN` row regardless of status | `BorrowerRepository` subquery (no status filter) |
| Payoff sums **all** recorded late fees despite the "unpaid" wording; `MIN(BALANCE_AFTER)` and `MAX(DUE_DATE)` selected independently; schedule-driven (ignores payment history); fully amortized loan pays off at `0` + fees; accrual floored at zero via the funded-date fallback | `ServicingCalculator.GetPayoffAmount` |
| Zero annual income throws `DivideByZeroException`; borrower id parsed only after rules pass; strict `<`/`>` boundaries | `EligibilityEngine` |
| Unknown product / non-positive term throw `ArgumentException` with the exact legacy message | `LoanCalculator` |
| `SEQ_PAYMENT` seed/sequence collision hazard | documented on `LoanRepository` (data layer owns the sequence DDL) |

## Rule disposition — all 55 catalog IDs

Statuses: **Domain** = implemented as executable business logic here; **Data** =
schema/DDL owned by the parallel database session (this layer only codes against it);
**API** = realized by the API surface per the contract; **N/A** = not behavioral in a
web service, justified below.

| Rule | Status | Where / justification |
| --- | --- | --- |
| BR-ELG-001..008 | Domain | `EligibilityEngine.Evaluate` — exact boundaries, product tables, `DivideByZeroException`, evaluation order. |
| BR-ELG-009 | Domain | `EligibilityEngine` — decision + byte-exact `resultText` (approval block incl. newlines/format specifiers, `"DECLINED\n" + reason`). |
| BR-ELG-010 | Domain | `ApplicationPersistence.Build` — status `SUBMITTED`, DTI/LTV `Math.Round(x, 4)` ToEven; id/timestamp come from `seq_loan_application`/DB clock in `ApplicationRepository`. |
| BR-ELG-011 | Domain/API | Legacy `decimal.Parse`/`int.Parse` failures ⇒ `"One or more fields contain invalid numbers."`; the API maps malformed request bodies to HTTP 400 with that exact text (`Program.cs` middleware). Borrower id is consumed only after rules pass (`/api/applications` evaluates, then persists). |
| BR-PQL-001 | Domain | `PrequalificationEngine` — independent threshold copy (LEND-3987), exact `"None — refer to special assets"` fallback. |
| BR-PQL-002 | Domain/Data | Semantics in `BorrowerSearchSemantics` (bound term, wildcards, case rules); `BorrowerRepository` executes the equivalent SQL with no added logic. |
| BR-PRC-001..006 | Domain | `LoanCalculator` — base rate, spread, LTV adjustment, discount, floor/cap + AwayFromZero, fee minimums and TERM cap. |
| BR-PRC-007 | Domain | `LoanCalculator` — `ArgumentException("Unknown product type: " + productType)` from both entry points. |
| BR-AMT-001..002 | Domain | `LoanCalculator.MonthlyPayment` / `BuildSchedule` — double round-trip, per-period AwayFromZero, final/early drift absorption, stop at non-positive balance. |
| BR-AMT-003 | Domain | `ArgumentException("termMonths must be positive")`. |
| BR-SVC-001 | Domain | `ServicingCalculator.CalcLateFee` — PL/SQL `PKG_LENDING.CALC_LATE_FEE` moved into .NET; grace ≤ 10 days, 5%, min 25 / max 150. Postgres stays logic-free. |
| BR-SVC-002 | Domain | `ServicingCalculator.GetPayoffAmount` — `GET_PAYOFF_AMOUNT` moved into .NET; actual/365 accrual, independent MIN/MAX row selection, funded-date fallback, all quirks above. |
| BR-SVC-003 | N/A | Oracle stored-function invocation plumbing (`ReturnValue` parameter, `Convert.ToDecimal(ret.Value.ToString())`, connection-per-call). The stored functions no longer exist — their logic is BR-SVC-001/002 in Domain — so there is no invocation to marshal. No behavior visible to callers is lost. |
| BR-SVC-004 | API | Legacy always quoted payoff as of the client's `DateTime.Today`. The contract exposes `asOf` explicitly (`GET /api/loans/{id}/payoff?asOf=...`); "today" becomes the caller's choice of `asOf`, day-granular exactly like the Oracle `DATE` bind. |
| BR-UI-001 | Domain | `PricingQuoteEngine` — `"Rate: {0.00} %"`, `"Origination fee: {C2}"`, `"Monthly payment: {C2}"` byte-exact. |
| BR-UI-002 | Domain/API | Quote returns the full schedule rows the grid bound. |
| BR-UI-003 / BR-UI-007 | API | Legacy validation/error message boxes map to HTTP 400 `{ "message": "<legacy text>" }` per the contract. |
| BR-UI-004 | Domain | `ServicingCalculator.LateFeeLabel` — `"Late fee: {C2}"`. |
| BR-UI-005 | Domain | LEND-5102 preserved in `PricingQuoteEngine.LateFeeFromPricingScreen` (loan amount reused as payment amount). |
| BR-UI-006 | Domain | Result panel strings/colors: `EligibilityResult.ResultText` + `ResultLabelColor` (`ForestGreen`/`Firebrick`). |
| BR-UI-008 | Domain | `"Pre-qualified products: "` prefix in `PrequalificationEngine`. |
| BR-UI-009 | API | `GET /api/loans/{id}/schedule` returns the legacy `PAYMENT_SCHEDULE` column set ordered by period. |
| BR-UI-010 | Domain/API | `ScheduleCsv` — legacy uppercase header, CRLF after every row; served at `/api/loans/{id}/schedule.csv`. Legacy wrote to `Path.GetTempPath()`; a web API returns the bytes instead — the file destination is client concern, format is preserved. |
| BR-UI-011 | N/A | `"Payoff as of {ToShortDateString()}: {C2}"` label. The contract's payoff response is structured JSON (`asOf`, `payoff`); the composed label is a WinForms presentation artifact not in the API contract. The underlying values are exact (BR-SVC-002/004). |
| BR-UI-012 | N/A | WinForms `DropDownList` ordering/default (TERM first). The API has no combo box; product strings are validated by BR-PRC-007 exactly as legacy. Ordering/default belongs to the new UI layer. |
| BR-UI-013 | N/A | Launcher window title, hard-coded status bar, modal navigation — WinForms shell presentation with no business effect; owned by the UI layer session. |
| BR-UI-014 | Domain/API | Blank deposit balance ⇒ `0m`: `depositBalance` is optional on `/api/pricing/quote` and defaults to `0m`. |
| BR-UI-015 | Domain | Culture pinned to en-US in `LegacyCulture` and at API startup so all format strings match the production en-US workstation output. |
| BR-CFG-001 | N/A | Oracle connection string + provider. Replaced by the Postgres connection string (`ConnectionStrings:Lending`); credentials are configuration, not behavior. |
| BR-CFG-002 | N/A | `Environment` app setting was **never read** by any legacy code path — nothing to migrate. |
| BR-CFG-003 | N/A | `StatementExportPath` was **never read**; legacy CSV export wrote to `Path.GetTempPath()` (see BR-UI-010) — nothing to migrate. |
| BR-DAT-001..010 | Data (parallel session) | Declarative schema (tables, `numeric(p,s)`, sequences, indexes, no triggers/stored code, no threshold enforcement in the DB). Owned by `database/postgres/schema.sql`; this layer's repositories code against those lower_snake_case names and put **no** logic in the database, satisfying BR-DAT-008/009 from this side. BR-DAT-010's PL/SQL schedule generator is superseded: booking (`POST /api/loans`) writes the schedule computed by `LoanCalculator.BuildSchedule`, keeping the amortization rules in exactly one place. |

## Parity gate

`tests/Contoso.Lending.ParityTests` loads **every record of every file** in
`parity/golden/*.json`:

- `CorpusIntegrityTests` asserts exactly **24 files** and **688 records** (silent
  under-collection fails the run) and that every record carries `ruleId` and
  `expected`/`expectedError`.
- One xUnit theory per business-rule ID (`BR_ELG_001` … `BR_SVC_002`), one theory
  case per golden record, so the run output shows per-rule case counts.
- Assertions are exact equality: no tolerance, no epsilon; decline/result strings
  byte-exact; thrown exceptions asserted on **type full name and message**.
- `BR-PQL-002` replays the search semantics against a test-only fixture mirroring
  the legacy seed data (the corpus was produced against that seed); no schema is
  authored here.

Run:

```
dotnet test tests/Contoso.Lending.ParityTests
```

## Unresolved / coordination notes

- `database/postgres/schema.sql` is produced by the parallel data-layer session.
  Repositories here follow `docs/data-profile.md` naming (lower_snake_case legacy
  identifiers, `numeric(p,s)`); if the landed schema diverges, only
  `src/Contoso.Lending.Data` SQL strings need adjusting — no rule code changes.
- Booking generates due dates as `fundedDate + n months`, matching the seed
  generator's `ADD_MONTHS(FUNDED_DATE, PERIOD_NO)`.
