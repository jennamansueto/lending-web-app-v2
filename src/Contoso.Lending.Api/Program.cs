using System.Globalization;
using System.Text.Json;
using Contoso.Lending.Api;
using Contoso.Lending.Data;
using Contoso.Lending.Domain;
using Microsoft.AspNetCore.Http.Json;
using Npgsql;

// BR-UI-015: pin en-US so legacy-format strings are byte-identical on any host.
CultureInfo.DefaultThreadCurrentCulture = LegacyCulture.EnUs;
CultureInfo.DefaultThreadCurrentUICulture = LegacyCulture.EnUs;

var builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("Lending")
    ?? "Host=localhost;Port=5432;Database=lending;Username=lending;Password=lending";
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<BorrowerRepository>();
builder.Services.AddSingleton<ApplicationRepository>();
builder.Services.AddSingleton<LoanRepository>();
builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);

var app = builder.Build();

// docs/api-contract.md: legacy message-box validation failures map to HTTP 400
// with { "message": "<legacy text>" }. BR-ELG-011 / BR-UI-007
// LoanApplicationForm.cs:149-158, PricingForm.cs:97-101.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (BadHttpRequestException)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { message = "One or more fields contain invalid numbers." });
    }
    catch (JsonException)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { message = "One or more fields contain invalid numbers." });
    }
});

// ---- Eligibility (BR-ELG-001..009) ----
app.MapPost("/api/eligibility/evaluate", (EligibilityRequest req) =>
{
    var result = EligibilityEngine.Evaluate(new EligibilityInput
    {
        Product = req.ProductType,
        Amount = req.Amount,
        TermMonths = req.TermMonths,
        AnnualIncome = req.AnnualIncome,
        MonthlyDebt = req.MonthlyDebt,
        CreditScore = req.CreditScore,
        CollateralValue = req.CollateralValue,
        YearsInBusiness = req.YearsInBusiness,
    });
    return Results.Ok(new
    {
        decision = result.Decision,
        declineReason = result.DeclineReason,
        dti = result.Dti,
        ltv = result.Ltv,
        estimatedPayment = result.EstPayment,
        resultText = result.ResultText,
        firedRuleId = result.FiredRuleId,
        ruleIds = result.RuleIds,
    });
});

// BR-ELG-010: persistence of an approved application. Evaluation is NOT implied;
// callers evaluate first, then persist, exactly as the legacy screen did
// (SaveApplication called only on the approval path, LoanApplicationForm.cs:143).
app.MapPost("/api/applications", async (EligibilityRequest req, ApplicationRepository applications) =>
{
    var result = EligibilityEngine.Evaluate(new EligibilityInput
    {
        Product = req.ProductType,
        Amount = req.Amount,
        TermMonths = req.TermMonths,
        AnnualIncome = req.AnnualIncome,
        MonthlyDebt = req.MonthlyDebt,
        CreditScore = req.CreditScore,
        CollateralValue = req.CollateralValue,
        YearsInBusiness = req.YearsInBusiness,
    });
    if (result.Decision != "APPROVED")
    {
        return Results.BadRequest(new { message = result.DeclineReason, ruleIds = result.RuleIds });
    }
    var persisted = ApplicationPersistence.Build(
        req.BorrowerId, req.ProductType, req.Amount, req.TermMonths, req.CreditScore,
        result.Dti!.Value, result.Ltv!.Value);
    int appId = await applications.InsertAsync(persisted);
    return Results.Ok(new { appId, ruleIds = new[] { "BR-ELG-010" } });
});

// ---- Pre-qualification (BR-PQL-001) ----
app.MapGet("/api/borrowers/{id:int}/prequalification", async (int id, BorrowerRepository borrowers) =>
{
    int? creditScore = await borrowers.GetCreditScoreAsync(id);
    if (creditScore is null) return Results.NotFound();
    var result = PrequalificationEngine.Evaluate(creditScore.Value);
    return Results.Ok(new
    {
        creditScore = creditScore.Value,
        prequalifiedProducts = result.PrequalifiedProducts,
        resultText = result.LabelText,
        ruleIds = new[] { "BR-PQL-001" },
    });
});

// ---- Pricing and amortization (BR-PRC-001..006, BR-AMT-001..002) ----
app.MapPost("/api/pricing/rate", (RateRequest req) =>
{
    var r = PricingBreakdown.Rate(req.ProductType, req.CreditScore, req.Ltv, req.DepositBalance);
    return Results.Ok(new
    {
        baseRate = r.BaseRate,
        riskSpread = r.RiskSpread,
        ltvAdjustment = r.LtvAdjustment,
        relationshipDiscount = r.RelationshipDiscount,
        rate = r.Rate,
        floored = r.Floored,
        capped = r.Capped,
        ruleIds = new[] { "BR-PRC-001", "BR-PRC-002", "BR-PRC-003", "BR-PRC-004", "BR-PRC-005" },
    });
});

app.MapPost("/api/pricing/origination-fee", (FeeRequest req) =>
{
    var f = PricingBreakdown.OriginationFee(req.Amount, req.ProductType);
    return Results.Ok(new
    {
        fee = f.Fee,
        minApplied = f.MinApplied,
        capApplied = f.CapApplied,
        ruleIds = new[] { "BR-PRC-006" },
    });
});

app.MapPost("/api/amortization/payment", (AmortizationRequest req) =>
{
    decimal payment = LoanCalculator.MonthlyPayment(req.Principal, req.AnnualRatePct, req.TermMonths);
    return Results.Ok(new { payment, ruleIds = new[] { "BR-AMT-001" } });
});

app.MapPost("/api/amortization/schedule", (AmortizationRequest req) =>
{
    decimal payment = LoanCalculator.MonthlyPayment(req.Principal, req.AnnualRatePct, req.TermMonths);
    var rows = LoanCalculator.BuildSchedule(req.Principal, req.AnnualRatePct, req.TermMonths);
    return Results.Ok(new
    {
        payment,
        rows = rows.Select(r => new
        {
            period = r.Period,
            payment = r.Payment,
            interest = r.Interest,
            principal = r.Principal,
            balance = r.Balance,
        }),
        ruleIds = new[] { "BR-AMT-001", "BR-AMT-002" },
    });
});

app.MapPost("/api/pricing/quote", (QuoteRequest req) =>
{
    // BR-UI-014 PricingForm.cs:85 — blank deposits box is treated as 0m.
    var quote = PricingQuoteEngine.Quote(req.ProductType, req.Amount, req.TermMonths,
        req.CreditScore, req.Ltv, req.DepositBalance ?? 0m);
    return Results.Ok(new
    {
        rate = quote.Rate,
        originationFee = quote.OriginationFee,
        monthlyPayment = quote.MonthlyPayment,
        rateLabel = quote.RateLabel,
        feeLabel = quote.FeeLabel,
        paymentLabel = quote.PaymentLabel,
        rows = quote.Schedule.Select(r => new
        {
            period = r.Period,
            payment = r.Payment,
            interest = r.Interest,
            principal = r.Principal,
            balance = r.Balance,
        }),
        ruleIds = new[]
        {
            "BR-PRC-001", "BR-PRC-002", "BR-PRC-003", "BR-PRC-004", "BR-PRC-005",
            "BR-PRC-006", "BR-AMT-001", "BR-AMT-002",
        },
    });
});

// ---- Servicing (BR-SVC-001..002) ----
app.MapPost("/api/servicing/late-fee", (LateFeeRequest req) =>
{
    decimal fee = ServicingCalculator.CalcLateFee(req.PaymentAmount, req.DaysLate);
    return Results.Ok(new { fee, ruleIds = new[] { "BR-SVC-001" } });
});

app.MapGet("/api/loans/{id:int}/payoff", async (int id, DateTime asOf, LoanRepository loans) =>
{
    var loan = await loans.GetLoanAsync(id);
    if (loan is null) return Results.NotFound();
    var schedule = await loans.GetScheduleEntriesAsync(id);
    decimal lateFees = await loans.GetLateFeeSumAsync(id);
    var quote = ServicingCalculator.GetPayoffQuote(
        loan.AnnualRate, loan.Principal, loan.FundedDate, schedule, lateFees, asOf);
    return Results.Ok(new
    {
        loanId = id,
        asOf = asOf.ToString("yyyy-MM-dd"),
        balance = quote.Balance,
        accruedInterest = quote.AccruedInterest,
        unpaidLateFees = quote.LateFees,
        payoff = quote.Payoff,
        ruleIds = new[] { "BR-SVC-002" },
    });
});

// ---- Data reads (BR-PQL-002, schedule views) ----
app.MapGet("/api/borrowers", async (string? search, BorrowerRepository borrowers) =>
{
    // BR-PQL-002 BorrowerLookupForm.cs:43 — bound term built by the Domain rule;
    // empty/whitespace search binds "%%" and returns every borrower.
    string boundTerm = BorrowerSearchSemantics.BuildBoundTerm(search ?? "");
    var rows = await borrowers.SearchAsync(boundTerm);
    return Results.Ok(new
    {
        boundTerm,
        rows = rows.Select(r => new
        {
            borrowerId = r.BorrowerId,
            legalName = r.LegalName,
            taxId = r.TaxId,
            creditScore = r.CreditScore,
            depositBalance = r.DepositBalance,
            yearsInBusiness = r.YearsInBusiness,
            activeLoans = r.ActiveLoans,
        }),
        ruleIds = new[] { "BR-PQL-002" },
    });
});

app.MapGet("/api/loans/{id:int}/schedule", async (int id, LoanRepository loans) =>
{
    var rows = await loans.GetScheduleAsync(id);
    return Results.Ok(new
    {
        rows = rows.Select(r => new
        {
            periodNo = r.PeriodNo,
            dueDate = r.DueDate.ToString("yyyy-MM-dd"),
            paymentAmt = r.PaymentAmt,
            interestAmt = r.InterestAmt,
            principalAmt = r.PrincipalAmt,
            balanceAfter = r.BalanceAfter,
        }),
        ruleIds = Array.Empty<string>(),
    });
});

app.MapGet("/api/loans/{id:int}/schedule.csv", async (int id, LoanRepository loans) =>
{
    var rows = await loans.GetScheduleAsync(id);
    // BR-UI-010 StatementsForm.cs:53-68 — legacy header and CRLF line endings.
    string csv = ScheduleCsv.Build(rows.Select(r =>
        (r.PeriodNo, r.DueDate, r.PaymentAmt, r.InterestAmt, r.PrincipalAmt, r.BalanceAfter)));
    return Results.Text(csv, "text/csv");
});

// ---- Workflow-facing booking ----
app.MapPost("/api/loans", async (BookLoanRequest req, LoanRepository loans) =>
{
    // The schedule written at booking is the service-computed amortization
    // (BR-AMT-001/002); the database stores rows, it computes nothing.
    var schedule = LoanCalculator.BuildSchedule(req.Principal, req.AnnualRate, req.TermMonths);
    int loanId = await loans.BookLoanAsync(req.AppId, req.BorrowerId, req.ProductType, req.Principal,
        req.AnnualRate, req.TermMonths, req.OrigFee, req.FundedDate, schedule);
    return Results.Ok(new { loanId, ruleIds = new[] { "BR-AMT-001", "BR-AMT-002" } });
});

app.Run("http://localhost:5080");
