# UI / Presentation layer (layer 4)

Angular 22 workspace under `ui/` composed with **@angular-architects/native-federation**
(Module Federation for esbuild builds). One shell plus one independently buildable remote per
legacy screen. All business behavior comes from the service API in `docs/api-contract.md`; the
UI parses input text, submits it, and renders returned values — nothing else.

## Workspace layout

| Project | Path | Dev port | Role |
|---|---|---|---|
| `shell` | `ui/shell/` | 4200 | `MainForm` equivalent: launcher nav + status bar, hosts remotes via router |
| `loan-application` | `ui/remotes/loan-application/` | 4201 | `LoanApplicationForm` |
| `pricing` | `ui/remotes/pricing/` | 4202 | `PricingForm` |
| `borrower-lookup` | `ui/remotes/borrower-lookup/` | 4203 | `BorrowerLookupForm` |
| `statements` | `ui/remotes/statements/` | 4204 | `StatementsForm` |
| mock API | `ui/mock-api/server.mjs` | 5080 | Dev-only mock of the service contract (not the service layer) |

Each remote exposes `./Component` (its root standalone component) through
`federation.config.mjs`; the shell maps route paths to remotes via
`ui/shell/public/federation.manifest.json` and `loadRemoteModule`. Every remote builds and runs
standalone (`ng build <name>`, `ng serve <name>`), and no state is shared between remotes —
each talks only to the service API.

## Screen → remote → API endpoints

| Legacy screen | Remote | Endpoints used |
|---|---|---|
| `MainForm` (launcher, status bar) | `shell` | none (navigation only) |
| `LoanApplicationForm` | `loan-application` | `POST /api/eligibility/evaluate`; `POST /api/applications` (only after APPROVED) |
| `PricingForm` | `pricing` | `POST /api/pricing/quote` (rate + fee + payment + schedule); `POST /api/servicing/late-fee` |
| `BorrowerLookupForm` | `borrower-lookup` | `GET /api/borrowers?search=`; `GET /api/borrowers/{id}/prequalification` |
| `StatementsForm` | `statements` | `GET /api/loans/{id}/schedule`; `GET /api/loans/{id}/schedule.csv`; `GET /api/loans/{id}/payoff?asOf=<today>` |

`resultText` strings returned by the service are rendered **verbatim** (pre-line whitespace), and
the UI performs no thresholds, rate/fee/payment/DTI/LTV arithmetic, rounding of computed money
values, or eligibility decisions. The only client-side formatting is presentational rendering of
numbers the API returned (e.g. two-decimal grid cells, `$#,##0.00` for the payoff label — the
WinForms `ToString("C2")` equivalent).

## API base URL / mock switching

Each remote reads `environment.apiBaseUrl` from `src/environments/environment.ts`
(`http://localhost:5080` in dev, replaced by `environment.prod.ts` via Angular file replacement in
production builds). `ui/mock-api/server.mjs` (`node ui/mock-api/server.mjs`) is development
infrastructure only: it implements the exact contract shapes and legacy result strings so the UI
could be built in parallel with the service layer. Pointing `apiBaseUrl` at the real service
requires no UI change.

## Reproduced legacy quirks

| Rule / ticket | Where | Behavior reproduced |
|---|---|---|
| LEND-5102 / BR-UI-005 | `pricing` | "Late Fee (DB)" reuses the **Loan Amount ($)** box as the late-fee payment amount — not the computed monthly payment. Not fixed. |
| BR-UI-014 | `pricing` | Blank **Deposit Balance** is treated as `0` (all other blank numerics are validation errors). |
| LEND-3987 | `borrower-lookup` | Pre-qualification hint thresholds are the application screen's score bands; served by `GET /api/borrowers/{id}/prequalification` so the duplication now lives in one service. The UI renders the returned `resultText` verbatim. |
| BR-UI-008 | `borrower-lookup` | First row auto-selected after a search fires the hint; any failure silently blanks the hint label (no error surfaced). |
| BR-UI-001..004 | `loan-application` | Legacy labels/order, product combo fixed to `TERM, LOC, EQUIP`, thousands-separator-tolerant numeric parsing; any parse failure shows the exact message box `One or more fields contain invalid numbers.` (caption `Validation`). |
| BR-UI-006 | `loan-application` | Result label starts as `Enter application details and press Submit.`; approval text renders dark-green, declines firebrick, exactly as returned in `resultText`. |
| BR-UI-007 | `loan-application` | Borrower ID is parsed **after** eligibility succeeds — a bad borrower ID still shows the approval, then errors on save, matching the legacy ordering. |
| BR-UI-012 | `pricing` | Schedule grid shows raw two-decimal values with no currency symbols; `Rate:` label bold, fee/payment labels plain. |
| BR-UI-009 | `statements` | "Load Schedule" has no error handling: an unknown loan just leaves the grid empty, no message. |
| BR-UI-010 | `statements` | "Export CSV" guard checks whether a schedule was ever *loaded* (not the row count); message `Load a schedule first.`. CSV keeps the legacy header and CRLF endings (served by `schedule.csv`). Legacy wrote to `%TEMP%\loan_<id>_schedule.csv`; the web equivalent is a browser download with the same file name and an `Exported to …` message box (documented divergence — browsers cannot write arbitrary paths). |
| BR-UI-011 | `statements` | Payoff label `Payoff as of M/d/yyyy: $#,##0.00` in bold; failures show message box `Payoff lookup failed: <message>` with caption `Error`. |
| BR-UI-013 | `shell` | Status bar hard-codes `Connected: PROD (ORCL/lending)   |   User: LENDINGDESK` regardless of actual connectivity, mirroring `MainForm`. |

## Running locally

```bash
cd ui
npm ci
node mock-api/server.mjs &        # contract mock on :5080
npx ng serve loan-application &   # :4201
npx ng serve pricing &            # :4202
npx ng serve borrower-lookup &    # :4203
npx ng serve statements &         # :4204
npx ng serve shell                # :4200 — loads all remotes
```
