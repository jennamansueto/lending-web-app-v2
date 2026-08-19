# Service API contract (layer 2)

Authoritative contract for the service layer. The workflow layer (3) and every UI remote (4) may
only reach business behavior through these endpoints. Base URL in development:
`http://localhost:5080`.

Conventions:
- All money and rate values are JSON numbers serialized from .NET `decimal` (never `double`).
- `resultText` fields carry the **exact** legacy user-visible string, `\n` included; they are part
  of the behavioral contract and are parity-tested verbatim.
- Every endpoint response includes `ruleIds`: the business-rule IDs that fired, for traceability.
- Validation failures that the legacy UI surfaced as a message box (`One or more fields contain
  invalid numbers.`) map to HTTP 400 with `{ "message": "<legacy text>" }`.

## Eligibility (BR-ELG-001..009)

`POST /api/eligibility/evaluate`

```jsonc
// request
{ "borrowerId": 1, "productType": "TERM", "amount": 450000, "termMonths": 60,
  "annualIncome": 900000, "monthlyDebt": 3000, "creditScore": 700,
  "collateralValue": 700000, "yearsInBusiness": 12 }
// response
{ "decision": "APPROVED" | "DECLINED",
  "declineReason": null,                       // exact legacy reason string when DECLINED
  "dti": 0.4123, "ltv": 0.6429,                // unrounded decimals
  "estimatedPayment": 8802.13,                 // payment at base rate (BR-ELG-005 input)
  "resultText": "APPROVED FOR UNDERWRITING\nDTI: 0.412   LTV: 0.643\nEst. payment at base rate: $8,802.13",
  "firedRuleId": "BR-ELG-009", "ruleIds": ["BR-ELG-001", "..."] }
```

Rule evaluation order is observable behavior (first failing rule wins) and must match the legacy
order: amount min -> amount max -> term -> credit score -> DTI -> collateral present -> LTV ->
LOC years-in-business.

`POST /api/applications` — persists an approved application (legacy `SaveApplication`, DTI/LTV
rounded to 4 dp, status `SUBMITTED`). Returns `{ "appId": 1000 }`. Evaluation is **not** implied:
callers evaluate first, then persist, exactly as the legacy screen did.

## Pre-qualification (BR-PQL-001)

`GET /api/borrowers/{id}/prequalification` ->
`{ "creditScore": 742, "prequalifiedProducts": "TERM, LOC, EQUIP", "resultText": "Pre-qualified products: TERM, LOC, EQUIP" }`

## Pricing and amortization (BR-PRC-001..006, BR-AMT-001..002)

- `POST /api/pricing/rate` `{ productType, creditScore, ltv, depositBalance }` ->
  `{ "baseRate": 6.50, "riskSpread": 0.35, "ltvAdjustment": 0.00, "relationshipDiscount": 0.25, "rate": 6.60, "floored": false, "capped": false }`
- `POST /api/pricing/origination-fee` `{ amount, productType }` -> `{ "fee": 4500.00, "minApplied": false, "capApplied": false }`
- `POST /api/amortization/payment` `{ principal, annualRatePct, termMonths }` -> `{ "payment": 8802.13 }`
- `POST /api/amortization/schedule` `{ principal, annualRatePct, termMonths }` ->
  `{ "payment": 8802.13, "rows": [ { "period": 1, "payment": 8802.13, "interest": 2437.50, "principal": 6364.63, "balance": 443635.37 } ] }`
- `POST /api/pricing/quote` — the composite the pricing screen uses: rate + fee + payment + schedule in one call.

## Servicing (BR-SVC-001..002)

- `POST /api/servicing/late-fee` `{ "paymentAmount": 1000, "daysLate": 15 }` -> `{ "fee": 50.00 }`
  (10-day grace, 5%, min $25, cap $150 — Oracle `ROUND` half-away-from-zero semantics.)
- `GET /api/loans/{id}/payoff?asOf=2026-08-19` ->
  `{ "loanId": 5001, "asOf": "2026-08-19", "balance": 383142.11, "accruedInterest": 1512.44, "unpaidLateFees": 75.00, "payoff": 384729.55 }`

## Data reads (layer 1 through layer 2 — the UI never touches the database)

- `GET /api/borrowers?search=<term>` — case-insensitive substring match on legal name **or** tax
  id, ordered by legal name; returns the legacy grid columns including `activeLoans`.
- `GET /api/loans/{id}/schedule` — `PAYMENT_SCHEDULE` rows, ordered by period, legacy column set.
- `GET /api/loans/{id}/schedule.csv` — CSV with the legacy header and CRLF line endings.

## Workflow-facing endpoints

The workflow layer uses only: `POST /api/eligibility/evaluate`, `POST /api/pricing/quote`,
`POST /api/applications`, and `POST /api/loans` (booking). It performs no arithmetic and applies no
thresholds of its own; it stores decisions returned by the service and sequences the steps.

`POST /api/loans` `{ appId, borrowerId, productType, principal, annualRate, termMonths, origFee, fundedDate }`
-> `{ "loanId": 5010 }`, and writes the amortization schedule returned by the service.
