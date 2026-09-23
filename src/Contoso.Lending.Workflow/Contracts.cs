namespace Contoso.Lending.Workflow;

/// <summary>
/// Wire shapes for the layer-2 service API (see docs/api-contract.md). They are plain data
/// carriers: this layer never inspects the numbers it forwards, it only moves them between the
/// service endpoints and the workflow result.
/// </summary>
public sealed record EligibilityRequest(
    int BorrowerId,
    string ProductType,
    decimal Amount,
    int TermMonths,
    decimal AnnualIncome,
    decimal MonthlyDebt,
    int CreditScore,
    decimal CollateralValue,
    int YearsInBusiness);

public sealed record EligibilityResponse(
    string Decision,
    string? DeclineReason,
    decimal? Dti,
    decimal? Ltv,
    decimal? EstimatedPayment,
    string ResultText,
    string FiredRuleId,
    IReadOnlyList<string> RuleIds);

public sealed record CreateApplicationRequest(
    int BorrowerId,
    string ProductType,
    decimal Amount,
    int TermMonths,
    int CreditScore,
    decimal? Dti,
    decimal? Ltv);

public sealed record CreateApplicationResponse(int AppId);

public sealed record QuoteRequest(
    string ProductType,
    decimal Amount,
    int TermMonths,
    int CreditScore,
    decimal? Ltv,
    decimal DepositBalance);

public sealed record RateResponse(
    decimal BaseRate,
    decimal RiskSpread,
    decimal LtvAdjustment,
    decimal RelationshipDiscount,
    decimal Rate,
    bool Floored,
    bool Capped,
    IReadOnlyList<string> RuleIds);

public sealed record ScheduleRow(
    int Period,
    decimal Payment,
    decimal Interest,
    decimal Principal,
    decimal Balance);

public sealed record QuoteResponse(
    RateResponse Rate,
    decimal OriginationFee,
    bool FeeMinApplied,
    bool FeeCapApplied,
    decimal Payment,
    IReadOnlyList<ScheduleRow> Rows,
    IReadOnlyList<string> RuleIds);

public sealed record BookLoanRequest(
    int AppId,
    int BorrowerId,
    string ProductType,
    decimal Principal,
    decimal AnnualRate,
    int TermMonths,
    decimal OrigFee,
    DateOnly FundedDate);

public sealed record BookLoanResponse(int LoanId);

/// <summary>Workflow input: the intake form plus the two values booking needs.</summary>
public sealed record LoanOriginationInput(
    int BorrowerId,
    string ProductType,
    decimal Amount,
    int TermMonths,
    decimal AnnualIncome,
    decimal MonthlyDebt,
    int CreditScore,
    decimal CollateralValue,
    int YearsInBusiness,
    decimal DepositBalance,
    DateOnly FundedDate);

/// <summary>
/// Everything the origination process produced. Every value is a value the service returned;
/// none is computed here.
/// </summary>
public sealed record LoanOriginationResult(
    string Decision,
    string? DeclineReason,
    string ResultText,
    string FiredRuleId,
    decimal? Dti,
    decimal? Ltv,
    decimal? EstimatedPayment,
    int? AppId,
    int? LoanId,
    decimal? Rate,
    decimal? OriginationFee,
    decimal? Payment,
    IReadOnlyList<ScheduleRow>? Schedule);

/// <summary>Answer to the <c>GetStatus</c> query: where the process is and what it stored.</summary>
public sealed record OriginationStatus(
    string Stage,
    string? Decision,
    string? DeclineReason,
    int? AppId,
    int? LoanId,
    IReadOnlyList<string> Reviewers);

/// <summary>Stage names are workflow state, not business state.</summary>
public static class OriginationStage
{
    public const string Submitted = "SUBMITTED";
    public const string EvaluatingEligibility = "EVALUATING_ELIGIBILITY";
    public const string Declined = "DECLINED";
    public const string PersistingApplication = "PERSISTING_APPLICATION";
    public const string Pricing = "PRICING";
    public const string Booking = "BOOKING";
    public const string Completed = "COMPLETED";
}

/// <summary>
/// The only decision string the workflow compares against. The service decides; this layer
/// sequences on the answer it was given.
/// </summary>
public static class Decisions
{
    public const string Approved = "APPROVED";
}
