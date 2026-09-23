# Layer 4 — UI / Presentation

Angular micro frontends that replace the WinForms desktop client. A shell host loads one
independently-buildable remote per legacy screen over Module Federation (Nx `@nx/angular`,
runtime/dynamic federation via `ui/shell/public/module-federation.manifest.json`).

The UI contains **no business rules**. Every decision, rate, fee, payment, schedule row, payoff and
result string comes from the layer-2 API; the screens only collect input, call the service and
render what comes back verbatim. The only client-side validation is the legacy numeric parse
message box (`One or more fields contain invalid numbers.`).

## Projects

| Project | Path | Port | Legacy form |
| --- | --- | --- | --- |
| `shell` | `ui/shell` | 4200 | `MainForm` launcher |
| `loanApplication` | `ui/remotes/loan-application` | 4201 | `LoanApplicationForm` |
| `pricing` | `ui/remotes/pricing` | 4202 | `PricingForm` |
| `borrowerLookup` | `ui/remotes/borrower-lookup` | 4203 | `BorrowerLookupForm` |
| `statements` | `ui/remotes/statements` | 4204 | `StatementsForm` |
| `@lending/api` | `ui/libs/api` | — | shared HTTP client, formatting, message box |

## Screen → remote → API

| Screen | Remote | Control | Endpoint |
| --- | --- | --- | --- |
| New Loan Application | `loanApplication` | `Check Eligibility & Submit` | `POST /api/eligibility/evaluate`, then `POST /api/applications` when the service returns `APPROVED` |
| Pricing & Amortization | `pricing` | `Price Loan` | `POST /api/pricing/quote` (rate, origination fee, monthly payment, amortization rows) |
| Pricing & Amortization | `pricing` | `Late Fee (DB)` | `POST /api/servicing/late-fee` |
| Borrower Lookup | `borrowerLookup` | `Search` | `GET /api/borrowers?search=<term>` |
| Borrower Lookup | `borrowerLookup` | grid row selection | `GET /api/borrowers/{id}/prequalification` |
| Statements Export | `statements` | `Load Schedule` | `GET /api/loans/{id}/schedule` |
| Statements Export | `statements` | `Export CSV` | `GET /api/loans/{id}/schedule.csv` |
| Statements Export | `statements` | `Payoff Quote (DB)` | `GET /api/loans/{id}/payoff` |
| Shell | `shell` | status bar connectivity check | `GET /api/borrowers?search=` |

## Running

Layers 1 and 2 first (see `docs/data-layer.md`): Postgres up, migrator run, API listening on
`http://localhost:5080`. Each dev server proxies `/api` there via `ui/proxy.conf.json`.

```bash
cd ui
npm install
npx nx run-many -t serve -p shell loanApplication pricing borrowerLookup statements
# shell: http://localhost:4200
```

`npx nx serve shell` alone also starts the remotes it depends on. A remote can be run on its own
(`npx nx serve pricing` → http://localhost:4202) — that is how each screen is exercised standalone.

Standalone production builds:

```bash
npx nx build loanApplication
npx nx build pricing
npx nx build borrowerLookup
npx nx build statements
npx nx build shell
```

The shell resolves remotes at runtime from `ui/shell/public/module-federation.manifest.json`, so
remote URLs are changed per environment without rebuilding the host.

## Decimals

Service money/rate values are .NET `decimal`s. `@lending/api` reads every response as text and
re-parses it with `parseDecimalJson`, which quotes numeric literals so the service's digits survive
as strings instead of being coerced to JavaScript floats. Rendering adds a currency symbol,
thousands separators or a `%` suffix only — no rounding or rescaling. Grids print the service digits
exactly as received.

## Preserved quirks

- **LEND-5102** — `Late Fee (DB)` on the pricing screen sends the **Loan Amount** field as the
  payment amount, exactly as the legacy form did. See
  `ui/remotes/pricing/src/app/remote-entry/entry.ts`.
- **LEND-3987** — the pre-qualification hint duplicates the eligibility credit thresholds. That
  duplication lives in the service (BR-PQL-001); the lookup screen just renders the returned
  `resultText`, so the divergence stays observable rather than being "fixed" in the UI.
- The legacy `MessageBox.Show("One or more fields contain invalid numbers.")` behavior is
  reproduced as a modal dialog. It is the only client-side validation.
- Approval results render green, declines red, as the legacy result panel did; the text itself is
  the service's `resultText`/decline reason, unmodified.
- Grid column headers keep the legacy database-style names (`BORROWER_ID`, `PERIOD_NO`, …).

## Boundaries

- No business state is shared between remotes; they communicate only through the service API.
- No direct database access and no mock or stub data — the committed app talks to the real API.
