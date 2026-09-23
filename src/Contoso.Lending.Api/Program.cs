using System.Globalization;
using System.Text;
using Contoso.Lending.Api;
using Contoso.Lending.Domain;

// Result strings are behavior (BR-UI-002/006), so the process runs under the legacy desk's
// culture regardless of the host locale.
CultureInfo.DefaultThreadCurrentCulture = LegacyCulture.EnUs;
CultureInfo.DefaultThreadCurrentUICulture = LegacyCulture.EnUs;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5080");
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton(new LendingRepository(
    Environment.GetEnvironmentVariable("POSTGRES_CONN")
    ?? "Host=localhost;Database=lending;Username=lending;Password=lending_pw_2014"));

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();

// Legacy `FormatException` path: the intake screen showed a message box with this text.
const string InvalidNumbers = "One or more fields contain invalid numbers.";

app.MapPost("/api/eligibility/evaluate", (EligibilityRequestDto request) =>
{
    if (!KnownProduct(request.ProductType)) return Results.BadRequest(new ErrorDto(InvalidNumbers));
    var result = EligibilityEngine.Evaluate(new EligibilityRequest(
        request.ProductType, request.Amount, request.TermMonths, request.AnnualIncome,
        request.MonthlyDebt, request.CreditScore, request.CollateralValue, request.YearsInBusiness));
    return Results.Ok(new EligibilityResponseDto(
        result.Decision, result.DeclineReason, result.Dti, result.Ltv, result.EstimatedPayment,
        result.ResultText, result.FiredRuleId, result.RuleIds));
})
.WithName("EvaluateEligibility");

app.MapPost("/api/applications", async (CreateApplicationRequestDto request, LendingRepository repository, CancellationToken ct) =>
{
    if (!KnownProduct(request.ProductType)) return Results.BadRequest(new ErrorDto(InvalidNumbers));
    int appId = await repository.InsertApplicationAsync(request, ct);
    return Results.Ok(new CreateApplicationResponseDto(appId));
})
.WithName("CreateApplication");

app.MapGet("/api/borrowers/{id:int}/prequalification", async (int id, LendingRepository repository, CancellationToken ct) =>
{
    int? creditScore = await repository.GetBorrowerCreditScoreAsync(id, ct);
    if (creditScore is null) return Results.NotFound();
    var result = PreQualificationEngine.Evaluate(creditScore.Value);
    return Results.Ok(new PrequalificationResponseDto(
        result.CreditScore, result.PrequalifiedProducts, result.ResultText, new[] { "BR-PQL-001" }));
})
.WithName("GetPrequalification");

app.MapPost("/api/pricing/rate", (RateRequestDto request) =>
{
    if (!KnownProduct(request.ProductType)) return Results.BadRequest(new ErrorDto(InvalidNumbers));
    return Results.Ok(RateResponse(request.ProductType, request.CreditScore, request.Ltv, request.DepositBalance));
})
.WithName("PriceRate");

app.MapPost("/api/pricing/origination-fee", (OriginationFeeRequestDto request) =>
{
    if (!KnownProduct(request.ProductType)) return Results.BadRequest(new ErrorDto(InvalidNumbers));
    var fee = PricingEngine.CalcOriginationFeeDetailed(request.Amount, request.ProductType);
    return Results.Ok(new OriginationFeeResponseDto(fee.Fee, fee.MinApplied, fee.CapApplied, new[] { "BR-PRC-006" }));
})
.WithName("CalculateOriginationFee");

app.MapPost("/api/amortization/payment", (PaymentRequestDto request) =>
{
    if (request.TermMonths <= 0) return Results.BadRequest(new ErrorDto(InvalidNumbers));
    return Results.Ok(new PaymentResponseDto(
        AmortizationEngine.MonthlyPayment(request.Principal, request.AnnualRatePct, request.TermMonths),
        new[] { "BR-AMT-001" }));
})
.WithName("MonthlyPayment");

app.MapPost("/api/amortization/schedule", (PaymentRequestDto request) =>
{
    if (request.TermMonths <= 0) return Results.BadRequest(new ErrorDto(InvalidNumbers));
    var rows = AmortizationEngine.BuildSchedule(request.Principal, request.AnnualRatePct, request.TermMonths);
    return Results.Ok(new ScheduleResponseDto(
        AmortizationEngine.MonthlyPayment(request.Principal, request.AnnualRatePct, request.TermMonths),
        rows.Select(ToRowDto).ToList(),
        new[] { "BR-AMT-001", "BR-AMT-002" }));
})
.WithName("AmortizationSchedule");

app.MapPost("/api/pricing/quote", (QuoteRequestDto request) =>
{
    if (!KnownProduct(request.ProductType) || request.TermMonths <= 0)
        return Results.BadRequest(new ErrorDto(InvalidNumbers));

    var rate = RateResponse(request.ProductType, request.CreditScore, request.Ltv, request.DepositBalance);
    var fee = PricingEngine.CalcOriginationFeeDetailed(request.Amount, request.ProductType);
    decimal payment = AmortizationEngine.MonthlyPayment(request.Amount, rate.Rate, request.TermMonths);
    var rows = AmortizationEngine.BuildSchedule(request.Amount, rate.Rate, request.TermMonths);
    return Results.Ok(new QuoteResponseDto(
        rate, fee.Fee, fee.MinApplied, fee.CapApplied, payment, rows.Select(ToRowDto).ToList(),
        new[] { "BR-PRC-001", "BR-PRC-002", "BR-PRC-003", "BR-PRC-004", "BR-PRC-005", "BR-PRC-006", "BR-AMT-001", "BR-AMT-002" }));
})
.WithName("PricingQuote");

app.MapPost("/api/servicing/late-fee", (LateFeeRequestDto request) =>
    Results.Ok(new LateFeeResponseDto(
        ServicingEngine.CalcLateFee(request.PaymentAmount, request.DaysLate),
        new[] { "BR-SVC-001" })))
.WithName("LateFee");

app.MapGet("/api/loans/{id:int}/payoff", async (int id, string? asOf, LendingRepository repository, CancellationToken ct) =>
{
    if (!TryParseAsOf(asOf, out DateOnly asOfDate)) return Results.BadRequest(new ErrorDto(InvalidNumbers));
    var state = await repository.GetPayoffStateAsync(id, ct);
    if (state is null) return Results.NotFound();
    var quote = ServicingEngine.CalcPayoff(state, asOfDate);
    return Results.Ok(new PayoffResponseDto(
        quote.LoanId, quote.AsOf.ToString("yyyy-MM-dd"), quote.Balance, quote.AccruedInterest,
        quote.UnpaidLateFees, quote.Payoff, new[] { "BR-SVC-002" }));
})
.WithName("PayoffQuote");

app.MapGet("/api/borrowers", async (string? search, LendingRepository repository, CancellationToken ct) =>
    Results.Ok(await repository.SearchBorrowersAsync(search ?? string.Empty, ct)))
.WithName("SearchBorrowers");

app.MapGet("/api/loans/{id:int}/schedule", async (int id, LendingRepository repository, CancellationToken ct) =>
    Results.Ok(await repository.GetScheduleAsync(id, ct)))
.WithName("LoanSchedule");

// Legacy `StatementsForm.btnExport_Click`: legacy header, CRLF line endings, no quoting.
app.MapGet("/api/loans/{id:int}/schedule.csv", async (int id, LendingRepository repository, CancellationToken ct) =>
{
    var rows = await repository.GetScheduleAsync(id, ct);
    var csv = new StringBuilder("PERIOD_NO,DUE_DATE,PAYMENT_AMT,INTEREST_AMT,PRINCIPAL_AMT,BALANCE_AFTER\r\n");
    foreach (var row in rows)
    {
        csv.Append(CultureInfo.InvariantCulture, $"{row.PeriodNo},{row.DueDate},{row.PaymentAmt},{row.InterestAmt},{row.PrincipalAmt},{row.BalanceAfter}\r\n");
    }
    return Results.Text(csv.ToString(), "text/csv");
})
.WithName("LoanScheduleCsv");

app.MapPost("/api/loans", async (BookLoanRequestDto request, LendingRepository repository, CancellationToken ct) =>
{
    if (!KnownProduct(request.ProductType) || request.TermMonths <= 0)
        return Results.BadRequest(new ErrorDto(InvalidNumbers));
    var schedule = AmortizationEngine.BuildSchedule(request.Principal, request.AnnualRate, request.TermMonths);
    int loanId = await repository.InsertLoanAsync(request, schedule, ct);
    return Results.Ok(new BookLoanResponseDto(loanId));
})
.WithName("BookLoan");

app.Run();

static bool KnownProduct(string productType) =>
    productType is "TERM" or "LOC" or "EQUIP";

static bool TryParseAsOf(string? asOf, out DateOnly value)
{
    if (string.IsNullOrWhiteSpace(asOf))
    {
        value = DateOnly.FromDateTime(DateTime.Today);
        return true;
    }
    return DateOnly.TryParseExact(asOf, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
}

static ScheduleRowDto ToRowDto(AmortizationRow row) =>
    new(row.Period, row.Payment, row.Interest, row.Principal, row.Balance);

static RateResponseDto RateResponse(string productType, int creditScore, decimal ltv, decimal depositBalance)
{
    var priced = PricingEngine.PriceRateDetailed(productType, creditScore, ltv, depositBalance);
    return new RateResponseDto(
        priced.BaseRate, priced.RiskSpread, priced.LtvAdjustment, priced.RelationshipDiscount,
        priced.Rate, priced.Floored, priced.Capped,
        new[] { "BR-PRC-001", "BR-PRC-002", "BR-PRC-003", "BR-PRC-004", "BR-PRC-005" });
}

/// <summary>Entry point exposed so the API regression tests can boot the real app.</summary>
public partial class Program;
