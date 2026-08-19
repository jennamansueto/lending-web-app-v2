namespace Contoso.Lending.Api;

// Request DTOs follow docs/api-contract.md field names exactly.

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

public sealed record RateRequest(string ProductType, int CreditScore, decimal Ltv, decimal DepositBalance);

public sealed record FeeRequest(decimal Amount, string ProductType);

public sealed record AmortizationRequest(decimal Principal, decimal AnnualRatePct, int TermMonths);

public sealed record QuoteRequest(
    string ProductType,
    decimal Amount,
    int TermMonths,
    int CreditScore,
    decimal Ltv,
    decimal? DepositBalance);

public sealed record LateFeeRequest(decimal PaymentAmount, int DaysLate);

public sealed record BookLoanRequest(
    int? AppId,
    int BorrowerId,
    string ProductType,
    decimal Principal,
    decimal AnnualRate,
    int TermMonths,
    decimal OrigFee,
    DateTime FundedDate);
