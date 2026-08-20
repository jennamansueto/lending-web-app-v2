# Service API contract

Authoritative contract for the .NET 8 service at `http://localhost:5080`.
Business logic is owned by the service/domain layer; the UI only parses input,
calls these endpoints, and renders returned values.

Conventions:

- Money and rate values are JSON numbers serialized from .NET `decimal`.
- `resultText` fields carry exact legacy strings, including embedded `\n`.
- Responses that expose business rules include `ruleIds`.
- Malformed numeric request bodies return HTTP 400 with
  `{ "message": "One or more fields contain invalid numbers." }`.
- A missing `borrowerId` on the persistence request is request-model validation,
  not a business-rule response; the loan-application UI validates it after an
  approved evaluation, matching the legacy dialog ordering.

## Eligibility and applications

### `POST /api/eligibility/evaluate`

Evaluation does not require a borrower ID.

```json
{
  "productType": "TERM",
  "amount": 100000,
  "termMonths": 60,
  "annualIncome": 600000,
  "monthlyDebt": 5000,
  "creditScore": 700,
  "collateralValue": 200000,
  "yearsInBusiness": 5
}
```

```json
{
  "decision": "APPROVED",
  "declineReason": null,
  "dti": 0.1391322,
  "ltv": 0.5,
  "estimatedPayment": 1956.61,
  "resultText": "APPROVED FOR UNDERWRITING\nDTI: 0.139   LTV: 0.500\nEst. payment at base rate: $1,956.61",
  "firedRuleId": "BR-ELG-009",
  "ruleIds": [
    "BR-ELG-001", "BR-ELG-002", "BR-ELG-003", "BR-ELG-004",
    "BR-ELG-005", "BR-ELG-006", "BR-ELG-007", "BR-ELG-008",
    "BR-ELG-009"
  ]
}
```

### `POST /api/applications`

Persists an approved application. The request requires `borrowerId`; callers
evaluate first and persist only on approval.

```json
{
  "borrowerId": 1,
  "productType": "TERM",
  "amount": 100000,
  "termMonths": 60,
  "annualIncome": 600000,
  "monthlyDebt": 5000,
  "creditScore": 700,
  "collateralValue": 200000,
  "yearsInBusiness": 5
}
```

```json
{ "appId": 1000, "ruleIds": ["BR-ELG-010"] }
```

The service recomputes eligibility and persists DTI/LTV using the legacy
four-decimal banker's rounding. A declined evaluation returns HTTP 400 with
the exact decline reason.

## Prequalification

### `GET /api/borrowers/{id}/prequalification`

```json
{
  "creditScore": 742,
  "prequalifiedProducts": "TERM, LOC, EQUIP",
  "resultText": "Pre-qualified products: TERM, LOC, EQUIP",
  "ruleIds": ["BR-PQL-001"]
}
```

## Pricing and amortization

### `POST /api/pricing/quote`

The pricing screen uses this composite endpoint.

Request:

```json
{
  "productType": "TERM",
  "amount": 100000,
  "termMonths": 60,
  "creditScore": 680,
  "ltv": 0.5,
  "depositBalance": 0
}
```

Response:

```json
{
  "rate": 7.2,
  "originationFee": 1000.0,
  "monthlyPayment": 1989.57,
  "rateLabel": "Rate: 7.20 %",
  "feeLabel": "Origination fee: $1,000.00",
  "paymentLabel": "Monthly payment: $1,989.57",
  "rows": [
    {
      "period": 1,
      "payment": 1989.57,
      "interest": 600.0,
      "principal": 1389.57,
      "balance": 98610.43
    }
  ],
  "ruleIds": [
    "BR-PRC-001", "BR-PRC-002", "BR-PRC-003", "BR-PRC-004",
    "BR-PRC-005", "BR-PRC-006", "BR-AMT-001", "BR-AMT-002"
  ]
}
```

`depositBalance` is optional and defaults to `0`.

The service also exposes the component endpoints:

- `POST /api/pricing/rate` with `{ productType, creditScore, ltv, depositBalance }`
  returns the rate breakdown and `rate`.
- `POST /api/pricing/origination-fee` with `{ amount, productType }` returns
  `{ fee, minApplied, capApplied, ruleIds }`.
- `POST /api/amortization/payment` with
  `{ principal, annualRatePct, termMonths }` returns `{ payment, ruleIds }`.
- `POST /api/amortization/schedule` with the same request returns
  `{ payment, rows, ruleIds }`, where each row has
  `{ period, payment, interest, principal, balance }`.

## Servicing

- `POST /api/servicing/late-fee` with `{ paymentAmount, daysLate }` returns
  `{ fee, ruleIds }`. The ten-day grace period, five-percent fee, `$25` minimum,
  and `$150` cap are legacy behavior. The pricing screen deliberately passes its
  loan amount for `paymentAmount` (LEND-5102).
- `GET /api/loans/{id}/payoff?asOf=2026-08-19` returns:

```json
{
  "loanId": 1,
  "asOf": "2026-08-19",
  "balance": 134655.19,
  "accruedInterest": 508.18,
  "unpaidLateFees": 0.0,
  "payoff": 135163.37,
  "ruleIds": ["BR-SVC-002"]
}
```

## Data reads

- `GET /api/borrowers?search=<term>` returns
  `{ boundTerm, rows: [...], ruleIds }`. Each row has
  `borrowerId`, `legalName`, `taxId`, `creditScore`, `depositBalance`,
  `yearsInBusiness`, and `activeLoans`.
- `GET /api/loans/{id}/schedule` returns
  `{ rows: [...], ruleIds }`. Each row has
  `periodNo`, `dueDate`, `paymentAmt`, `interestAmt`, `principalAmt`, and
  `balanceAfter`.
- `GET /api/loans/{id}/schedule.csv` returns the legacy CSV header and CRLF
  line endings as `text/csv`.

## Workflow-facing booking

The workflow uses evaluation, pricing quote, application persistence, and booking
endpoints. It performs no arithmetic or threshold evaluation.

### `POST /api/loans`

```json
{
  "appId": 1000,
  "borrowerId": 1,
  "productType": "TERM",
  "principal": 100000,
  "annualRate": 7.2,
  "termMonths": 60,
  "origFee": 1000,
  "fundedDate": "2026-08-20"
}
```

Returns `{ "loanId": 5010, "ruleIds": ["BR-AMT-001", "BR-AMT-002"] }`
and writes the service-computed amortization schedule.
