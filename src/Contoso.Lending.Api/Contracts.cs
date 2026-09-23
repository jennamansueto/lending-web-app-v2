namespace Contoso.Lending.Api;

public sealed record EligibilityRequestDto(
    int BorrowerId,
    string ProductType,
    decimal Amount,
    int TermMonths,
    decimal AnnualIncome,
    decimal MonthlyDebt,
    int CreditScore,
    decimal CollateralValue,
    int YearsInBusiness);

public sealed record EligibilityResponseDto(
    string Decision,
    string? DeclineReason,
    decimal? Dti,
    decimal? Ltv,
    decimal? EstimatedPayment,
    string ResultText,
    string FiredRuleId,
    IReadOnlyList<string> RuleIds);

public sealed record CreateApplicationRequestDto(
    int BorrowerId,
    string ProductType,
    decimal Amount,
    int TermMonths,
    int CreditScore,
    decimal Dti,
    decimal Ltv);

public sealed record CreateApplicationResponseDto(int AppId);

public sealed record PrequalificationResponseDto(
    int CreditScore,
    string PrequalifiedProducts,
    string ResultText,
    IReadOnlyList<string> RuleIds);

public sealed record RateRequestDto(string ProductType, int CreditScore, decimal Ltv, decimal DepositBalance);

public sealed record RateResponseDto(
    decimal BaseRate,
    decimal RiskSpread,
    decimal LtvAdjustment,
    decimal RelationshipDiscount,
    decimal Rate,
    bool Floored,
    bool Capped,
    IReadOnlyList<string> RuleIds);

public sealed record OriginationFeeRequestDto(decimal Amount, string ProductType);

public sealed record OriginationFeeResponseDto(decimal Fee, bool MinApplied, bool CapApplied, IReadOnlyList<string> RuleIds);

public sealed record PaymentRequestDto(decimal Principal, decimal AnnualRatePct, int TermMonths);

public sealed record PaymentResponseDto(decimal Payment, IReadOnlyList<string> RuleIds);

public sealed record ScheduleRowDto(int Period, decimal Payment, decimal Interest, decimal Principal, decimal Balance);

public sealed record ScheduleResponseDto(decimal Payment, IReadOnlyList<ScheduleRowDto> Rows, IReadOnlyList<string> RuleIds);

public sealed record QuoteRequestDto(
    string ProductType,
    decimal Amount,
    int TermMonths,
    int CreditScore,
    decimal Ltv,
    decimal DepositBalance);

public sealed record QuoteResponseDto(
    RateResponseDto Rate,
    decimal OriginationFee,
    bool FeeMinApplied,
    bool FeeCapApplied,
    decimal Payment,
    IReadOnlyList<ScheduleRowDto> Rows,
    IReadOnlyList<string> RuleIds);

public sealed record LateFeeRequestDto(decimal PaymentAmount, int DaysLate);

public sealed record LateFeeResponseDto(decimal Fee, IReadOnlyList<string> RuleIds);

public sealed record PayoffResponseDto(
    int LoanId,
    string AsOf,
    decimal Balance,
    decimal AccruedInterest,
    decimal UnpaidLateFees,
    decimal Payoff,
    IReadOnlyList<string> RuleIds);

public sealed record BorrowerRowDto(
    int BorrowerId,
    string LegalName,
    string TaxId,
    int CreditScore,
    decimal DepositBalance,
    int YearsInBusiness,
    int ActiveLoans);

public sealed record LoanScheduleRowDto(
    int PeriodNo,
    string DueDate,
    decimal PaymentAmt,
    decimal InterestAmt,
    decimal PrincipalAmt,
    decimal BalanceAfter);

public sealed record BookLoanRequestDto(
    int AppId,
    int BorrowerId,
    string ProductType,
    decimal Principal,
    decimal AnnualRate,
    int TermMonths,
    decimal OrigFee,
    DateOnly FundedDate);

public sealed record BookLoanResponseDto(int LoanId);

public sealed record ErrorDto(string Message);
