# Service layer (layer 2)

`src/Contoso.Lending.Domain` holds every business rule as pure, I/O-free code;
`src/Contoso.Lending.Api` is a thin minimal-API surface over it (port 5080) plus the Npgsql reads
the UI is not allowed to do itself. No orchestration or state machine lives here — the workflow
layer sequences calls, the service layer decides.

Behavior comes from the legacy desktop app (`jennamansueto/lending-desktop-app`, branch
`phase1-integration`) and is pinned by the 772-record golden corpus in `parity/golden/`.

## Rule ID -> code location

| Rule | Legacy source | Layer 2 location |
| --- | --- | --- |
| BR-ELG-001..009 | `Forms/LoanApplicationForm.btnSubmit_Click` | `Domain/EligibilityEngine.Evaluate` |
| BR-ELG-010 (order) | statement order in `btnSubmit_Click` | statement order in `EligibilityEngine.Evaluate` |
| BR-ELG-012 (DTI priced at base rate) | `btnSubmit_Click` | `EligibilityEngine.Evaluate` |
| BR-ELG-013 (DTI formula) | `btnSubmit_Click` | `EligibilityEngine.Evaluate` |
| BR-ELG-014 (LTV formula) | `btnSubmit_Click` | `EligibilityEngine.Evaluate` |
| BR-PQL-001 | `Forms/BorrowerLookupForm.grid_SelectionChanged` | `Domain/PreQualificationEngine.PrequalifiedProducts` |
| BR-PRC-001 base rate | `Core/LoanCalculator.GetBaseRate` | `Domain/PricingEngine.GetBaseRate` |
| BR-PRC-002 risk spread | `Core/LoanCalculator.GetRiskSpread` | `Domain/PricingEngine.GetRiskSpread` |
| BR-PRC-003 LTV adjustment | `Core/LoanCalculator.GetLtvAdjustment` | `Domain/PricingEngine.GetLtvAdjustment` |
| BR-PRC-004 relationship discount | `Core/LoanCalculator.GetRelationshipDiscount` | `Domain/PricingEngine.GetRelationshipDiscount` |
| BR-PRC-005 priced rate + floor/ceiling | `Core/LoanCalculator.PriceRate` | `Domain/PricingEngine.PriceRate` / `PriceRateDetailed` |
| BR-PRC-006 origination fee | `Core/LoanCalculator.CalcOriginationFee` | `Domain/PricingEngine.CalcOriginationFee` / `CalcOriginationFeeDetailed` |
| BR-PRC-007 unknown product throws | `Core/LoanCalculator` | `Domain/PricingEngine.GetBaseRate` / `CalcOriginationFee` |
| BR-AMT-001 monthly payment | `Core/LoanCalculator.MonthlyPayment` | `Domain/AmortizationEngine.MonthlyPayment` |
| BR-AMT-002 schedule | `Core/LoanCalculator.BuildSchedule` | `Domain/AmortizationEngine.BuildSchedule` |
| BR-AMT-003 non-positive term throws | `Core/LoanCalculator` | `Domain/AmortizationEngine.MonthlyPayment` |
| BR-AMT-004 due dates (`ADD_MONTHS`) | `database/seed/generate_schedules.sql` | `Api/LendingRepository.AddMonths` (booking only) |
| BR-SVC-001, BR-SVC-003..005 late fee | `plsql/pkg_lending.calc_late_fee` | `Domain/ServicingEngine.CalcLateFee` |
| BR-SVC-002, BR-SVC-006..010 payoff | `plsql/pkg_lending.calc_payoff` | `Domain/ServicingEngine.CalcPayoff` |
| BR-UI-002 eligibility result text | `Forms/LoanApplicationForm` | `Domain/EligibilityEngine` (`ResultText`) |
| BR-UI-005 borrower search | `Forms/BorrowerLookupForm` | `Api/LendingRepository.SearchBorrowersAsync` |
| BR-UI-006 pre-qualification label | `Forms/BorrowerLookupForm` | `Domain/PreQualificationEngine.LabelText` |

## Culture and rounding

- Every money and rate value is `decimal` end to end. `double` appears in exactly one place, the
  deliberate legacy round-trip in `MonthlyPayment` (see quirks).
- Rounding is always `Math.Round(x, 2, MidpointRounding.AwayFromZero)`, which is what both the
  legacy C# and Oracle `ROUND` do. Banker's rounding (the .NET default) is never used.
- Payoff accrual is actual/365 simple interest on whole days, matching Oracle date subtraction.
- Result strings are formatted with `LegacyCulture.EnUs` (`CultureInfo.GetCultureInfo("en-US")`)
  explicitly passed at every call site, so `0.412`, `$8,802.13` and the thousands separators do not
  depend on the host locale. The API process also sets `CultureInfo.DefaultThreadCurrentCulture`.
  `CorpusReconciliationTests` re-runs string-producing rules under `de-DE` to prove this.
- JSON responses serialize `decimal` values, so trailing scale (`6.50`, `4500.00`) survives and no
  binary floating point is introduced.

## Preserved legacy quirks

These are reproduced deliberately. None of them is a bug to fix here; fixing any of them breaks
parity.

1. **Double round-trip in the annuity formula** — `(decimal)Math.Pow((double)(1m + i), n)` in
   `AmortizationEngine.MonthlyPayment`. The exponentiation loses precision in `double` and the
   result is what the desk has quoted for a decade.
2. **LTV compared raw, displayed rounded** — `BR-ELG-007` tests the unrounded quotient while the
   approval text prints `0.000`, so a loan can be declined showing an LTV that looks within the cap.
3. **DTI computed before the collateral check** — a zero-collateral application is declined by
   `BR-ELG-006` but still returns a DTI (`BR-ELG-013` runs first).
4. **No zero-income guard** — `BR-ELG-013` divides by `annualIncome / 12` with no guard, exactly as
   the legacy form did.
5. **First-failing-rule-wins** — `BR-ELG-010`. Rule order is observable through `declineReason`, so
   the statement order in `Evaluate` is contract, not style.
6. **Duplicated pre-qualification thresholds (LEND-3987)** — `BR-PQL-001` carries its own 660/640/620
   bands, copied in 2016 and never reunified with the `BR-ELG-004` minimums. Implemented as a
   separate rule, not derived from eligibility.
7. **Late-fee grace is inclusive** — day 10 is free, day 11 is charged (`BR-SVC-003`).
8. **The $25 late-fee floor ignores the payment amount** — a $0.00 payment that is late still incurs
   $25 (`BR-SVC-005`).
9. **Payoff balance is `MIN(BALANCE_AFTER)`, not the latest row** — `BR-SVC-006`. With a normal
   amortizing schedule the minimum is the latest due row, but the aggregate is what shipped.
10. **Payoff falls back to the original principal and funded date** when no schedule row is due yet
    (`BR-SVC-006`), so accrual runs from funding.
11. **Negative accrual is floored at zero** for an as-of date before the last due date
    (`BR-SVC-008`).
12. **All recorded late fees are added, with no paid/unpaid filter** — `BR-SVC-009` sums
    `PAYMENT.LATE_FEE` for the loan even though the response field is named `unpaidLateFees`.
13. **Unknown product and non-positive term throw with the legacy message text**
    (`BR-PRC-007`, `BR-AMT-003`); the API maps those inputs to the legacy message-box text
    `One or more fields contain invalid numbers.` with HTTP 400.

## Parity

`bash parity/run-l2.sh` builds, runs one xUnit case per golden record (exact equality, no
tolerance), writes `parity/reports/l2-service.json` with per-rule totals and failing record ids, and
exits non-zero on any mismatch. `CorpusReconciliationTests` additionally fails if a golden file has
no test mapping, a mapped rule has no golden file, or the corpus size drifts from 20 files / 772
records.
