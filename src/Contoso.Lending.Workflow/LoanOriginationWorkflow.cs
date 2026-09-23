using Temporalio.Common;
using Temporalio.Workflows;
using TemporalWorkflow = Temporalio.Workflows.Workflow;

namespace Contoso.Lending.Workflow;

/// <summary>
/// Loan origination as state and sequencing only (layer 3, see docs/workflow-layer.md).
///
/// evaluate eligibility, then: DECLINED, record the service's decision and stop;
/// APPROVED, persist application, price, book, complete.
///
/// Every number in the result is a number the service returned. This type performs no
/// arithmetic, applies no threshold and makes no decision; the single branch is on the
/// <c>decision</c> string the eligibility endpoint produced. tests/Contoso.Lending.ArchTests
/// fails the build if that stops being true.
/// </summary>
[Workflow]
public class LoanOriginationWorkflow
{
    public const string TaskQueue = "loan-origination";

    // Retries and timeouts are orchestration policy, which is exactly what this layer owns.
    private static readonly ActivityOptions ServiceCall = new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(30),
        ScheduleToCloseTimeout = TimeSpan.FromMinutes(5),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromSeconds(10),
            MaximumAttempts = 5,
            NonRetryableErrorTypes = new[] { nameof(ServiceApiException) },
        },
    };

    private readonly List<string> reviewers = new();
    private string stage = OriginationStage.Submitted;
    private EligibilityResponse? eligibility;
    private int? appId;
    private int? loanId;

    [WorkflowRun]
    public async Task<LoanOriginationResult> RunAsync(LoanOriginationInput input)
    {
        stage = OriginationStage.EvaluatingEligibility;
        eligibility = await TemporalWorkflow.ExecuteActivityAsync(
            (OriginationActivities a) => a.EvaluateEligibilityAsync(new EligibilityRequest(
                input.BorrowerId,
                input.ProductType,
                input.Amount,
                input.TermMonths,
                input.AnnualIncome,
                input.MonthlyDebt,
                input.CreditScore,
                input.CollateralValue,
                input.YearsInBusiness)),
            ServiceCall);

        if (eligibility.Decision != Decisions.Approved)
        {
            stage = OriginationStage.Declined;
            return Declined(eligibility);
        }

        stage = OriginationStage.PersistingApplication;
        var application = await TemporalWorkflow.ExecuteActivityAsync(
            (OriginationActivities a) => a.PersistApplicationAsync(new CreateApplicationRequest(
                input.BorrowerId,
                input.ProductType,
                input.Amount,
                input.TermMonths,
                input.CreditScore,
                eligibility.Dti,
                eligibility.Ltv)),
            ServiceCall);
        appId = application.AppId;

        stage = OriginationStage.Pricing;
        var quote = await TemporalWorkflow.ExecuteActivityAsync(
            (OriginationActivities a) => a.PriceLoanAsync(new QuoteRequest(
                input.ProductType,
                input.Amount,
                input.TermMonths,
                input.CreditScore,
                eligibility.Ltv,
                input.DepositBalance)),
            ServiceCall);

        stage = OriginationStage.Booking;
        var booking = await TemporalWorkflow.ExecuteActivityAsync(
            (OriginationActivities a) => a.BookLoanAsync(new BookLoanRequest(
                application.AppId,
                input.BorrowerId,
                input.ProductType,
                input.Amount,
                quote.Rate.Rate,
                input.TermMonths,
                quote.OriginationFee,
                input.FundedDate)),
            ServiceCall);
        loanId = booking.LoanId;

        stage = OriginationStage.Completed;
        return new LoanOriginationResult(
            eligibility.Decision,
            eligibility.DeclineReason,
            eligibility.ResultText,
            eligibility.FiredRuleId,
            eligibility.Dti,
            eligibility.Ltv,
            eligibility.EstimatedPayment,
            application.AppId,
            booking.LoanId,
            quote.Rate.Rate,
            quote.OriginationFee,
            quote.Payment,
            quote.Rows);
    }

    /// <summary>Current stage plus the decision the service handed back, for operators and tests.</summary>
    [WorkflowQuery]
    public OriginationStatus GetStatus() => new(
        stage,
        eligibility?.Decision,
        eligibility?.DeclineReason,
        appId,
        loanId,
        reviewers.ToList());

    /// <summary>Records that a credit officer looked at the case. State only; changes no outcome.</summary>
    [WorkflowSignal]
    public Task RecordReviewerAsync(string reviewer)
    {
        reviewers.Add(reviewer);
        return Task.CompletedTask;
    }

    private static LoanOriginationResult Declined(EligibilityResponse decision) => new(
        decision.Decision,
        decision.DeclineReason,
        decision.ResultText,
        decision.FiredRuleId,
        decision.Dti,
        decision.Ltv,
        decision.EstimatedPayment,
        null,
        null,
        null,
        null,
        null,
        null);
}
