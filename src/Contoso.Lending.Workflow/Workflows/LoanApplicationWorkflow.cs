using Contoso.Lending.Workflow.Activities;
using Contoso.Lending.Workflow.Contracts;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;
using Wf = Temporalio.Workflows.Workflow;

namespace Contoso.Lending.Workflow.Workflows;

/// <summary>
/// Sequences the legacy "New Loan Application" screen: submit -> evaluate eligibility ->
/// persist on approval -> hand off to underwriting.
///
/// Every value in the result comes from the service layer untouched. The workflow's only
/// branch is on the <c>decision</c> string the service returned (and, for booking, on the
/// decision an underwriter signalled in): sequencing, not deciding. There is no arithmetic,
/// no threshold, no rounding and no string building anywhere in this file.
/// </summary>
[Workflow]
public class LoanApplicationWorkflow
{
    /// <summary>Status the service reports for a persisted application (docs/api-contract.md).</summary>
    private const string ServicePersistedStatus = "SUBMITTED";

    private static readonly ActivityOptions ServiceCall = new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(30),
        RetryPolicy = new RetryPolicy
        {
            InitialInterval = TimeSpan.FromSeconds(1),
            BackoffCoefficient = 2,
            MaximumInterval = TimeSpan.FromSeconds(10),
            MaximumAttempts = 5,
        },
    };

    private string status = "RECEIVED";
    private UnderwritingDecisionSignal? underwriting;

    [WorkflowQuery]
    public string Status => status;

    /// <summary>Underwriter hand-back. The workflow only routes on <paramref name="signal"/>.Decision.</summary>
    [WorkflowSignal]
    public Task SubmitUnderwritingDecision(UnderwritingDecisionSignal signal)
    {
        underwriting = signal;
        return Task.CompletedTask;
    }

    [WorkflowRun]
    public async Task<LoanApplicationResult> RunAsync(LoanApplicationInput input)
    {
        status = "EVALUATING";
        var eligibility = await Wf.ExecuteActivityAsync(
            (LendingServiceActivities a) => a.EvaluateEligibility(input.Application),
            ServiceCall);

        if (eligibility.Decision != EligibilityDecisions.Approved)
        {
            status = eligibility.Decision;
            return LoanApplicationResult.FromEligibility(eligibility, status);
        }

        status = "PERSISTING";
        var persisted = await Wf.ExecuteActivityAsync(
            (LendingServiceActivities a) => a.PersistApplication(input.Application),
            ServiceCall);

        status = ServicePersistedStatus;
        var result = LoanApplicationResult.FromEligibility(eligibility, status) with
        {
            AppId = persisted.AppId,
        };

        // Hand off to underwriting. Without a wait configured the workflow completes here,
        // mirroring the legacy screen, which stopped at "APPROVED FOR UNDERWRITING".
        status = "AWAITING_UNDERWRITING";
        result = result with { Status = status };
        if (input.UnderwritingDecisionTimeout is not { } timeout)
        {
            return result;
        }

        await Wf.WaitConditionAsync(() => underwriting is not null, timeout);
        if (underwriting is not { } decision)
        {
            return result;
        }

        if (decision.Decision != UnderwritingDecisions.Book)
        {
            status = decision.Decision;
            return result with { Status = status, UnderwritingNotes = decision.Notes };
        }

        status = "BOOKING";
        if (eligibility.Ltv is not { } ltv)
        {
            throw new ApplicationFailureException(
                "Service approved without an LTV; cannot book.", nonRetryable: true);
        }

        var quoteRequest = new PricingQuoteRequest(
            input.Application.ProductType,
            input.Application.Amount,
            input.Application.TermMonths,
            input.Application.CreditScore,
            ltv,
            input.DepositBalance);
        var quote = await Wf.ExecuteActivityAsync(
            (LendingServiceActivities a) => a.GetPricingQuote(quoteRequest),
            ServiceCall);

        var booked = await Wf.ExecuteActivityAsync(
            (LendingServiceActivities a) => a.BookLoan(new BookLoanRequest(
                persisted.AppId,
                input.Application.BorrowerId,
                input.Application.ProductType,
                input.Application.Amount,
                quote.Rate,
                input.Application.TermMonths,
                quote.OriginationFee,
                input.FundedDate ?? Wf.UtcNow.ToString("yyyy-MM-dd", null))),
            ServiceCall);

        status = "BOOKED";
        return result with
        {
            Status = status,
            UnderwritingNotes = decision.Notes,
            LoanId = booked.LoanId,
            Rate = quote.Rate,
            OriginationFee = quote.OriginationFee,
            Payment = quote.MonthlyPayment,
        };
    }
}

/// <summary>Decision strings owned by the service layer; the workflow only compares for routing.</summary>
public static class EligibilityDecisions
{
    public const string Approved = "APPROVED";
    public const string Declined = "DECLINED";
}

public static class UnderwritingDecisions
{
    public const string Book = "BOOK";
    public const string Withdrawn = "WITHDRAWN";
}

public record LoanApplicationInput(
    EligibilityRequest Application,
    decimal DepositBalance = 0m,
    string? FundedDate = null,
    TimeSpan? UnderwritingDecisionTimeout = null);

public record UnderwritingDecisionSignal(string Decision, string? Notes = null);

public record LoanApplicationResult(
    string Decision,
    string ResultText,
    string? DeclineReason,
    string? FiredRuleId,
    decimal? Dti,
    decimal? Ltv,
    decimal? EstimatedPayment,
    string Status)
{
    public long? AppId { get; init; }

    public long? LoanId { get; init; }

    public decimal? Rate { get; init; }

    public decimal? OriginationFee { get; init; }

    public decimal? Payment { get; init; }

    public string? UnderwritingNotes { get; init; }

    internal static LoanApplicationResult FromEligibility(EligibilityResult e, string status) =>
        new(e.Decision, e.ResultText, e.DeclineReason, e.FiredRuleId, e.Dti, e.Ltv, e.EstimatedPayment, status);
}
