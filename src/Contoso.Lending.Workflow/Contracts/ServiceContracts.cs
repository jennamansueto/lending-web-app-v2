namespace Contoso.Lending.Workflow.Contracts;

/// <summary>
/// Wire shapes of the layer-2 endpoints listed in <c>docs/api-contract.md</c>.
/// These are transport DTOs only: the workflow layer never inspects, compares or reformats
/// any field, it carries them between activities and returns them verbatim.
/// </summary>
public record EligibilityRequest(
    long BorrowerId,
    string ProductType,
    decimal Amount,
    int TermMonths,
    decimal AnnualIncome,
    decimal MonthlyDebt,
    int CreditScore,
    decimal CollateralValue,
    int YearsInBusiness);

public record EligibilityResult(
    string Decision,
    string? DeclineReason,
    decimal? Dti,
    decimal? Ltv,
    decimal? EstimatedPayment,
    string ResultText,
    string? FiredRuleId,
    IReadOnlyList<string>? RuleIds);

/// <summary>
/// <c>POST /api/applications</c> takes the same intake payload as the evaluation endpoint: the
/// service recomputes and rounds everything it persists, so the workflow forwards the intake
/// unchanged instead of echoing derived values back.
/// </summary>
public record PersistApplicationResult(long AppId);

public record PricingQuoteRequest(
    string ProductType,
    decimal Amount,
    int TermMonths,
    int CreditScore,
    decimal Ltv,
    decimal DepositBalance);

public record AmortizationRow(
    int Period,
    decimal Payment,
    decimal Interest,
    decimal Principal,
    decimal Balance);

public record PricingQuoteResult(
    decimal Rate,
    decimal OriginationFee,
    decimal MonthlyPayment,
    string? RateLabel,
    string? FeeLabel,
    string? PaymentLabel,
    IReadOnlyList<AmortizationRow>? Rows);

public record PayoffQuoteResult(
    long LoanId,
    string AsOf,
    decimal Balance,
    decimal AccruedInterest,
    decimal UnpaidLateFees,
    decimal Payoff);
