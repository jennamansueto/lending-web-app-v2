using Contoso.Lending.Workflow.Activities;
using Contoso.Lending.Workflow.Contracts;
using Temporalio.Common;
using Temporalio.Workflows;
using Wf = Temporalio.Workflows.Workflow;

namespace Contoso.Lending.Workflow.Workflows;

/// <summary>
/// Sequences the legacy "Statements Export" payoff quote: quote -> hold the quote as state for
/// downstream delivery. The as-of date is supplied by the caller (the legacy screen passed
/// "today"); the workflow neither defaults nor computes it beyond that fallback and returns the
/// service's numbers verbatim.
/// </summary>
[Workflow]
public class PayoffQuoteWorkflow
{
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

    [WorkflowQuery]
    public string Status => status;

    [WorkflowRun]
    public async Task<PayoffQuoteResult> RunAsync(PayoffQuoteInput input)
    {
        status = "QUOTING";
        var quote = await Wf.ExecuteActivityAsync(
            (LendingServiceActivities a) => a.GetPayoffQuote(new PayoffQuoteRequestArgs(
                input.LoanId,
                input.AsOf ?? Wf.UtcNow.ToString("yyyy-MM-dd", null))),
            ServiceCall);

        status = "QUOTED";
        return quote;
    }
}

public record PayoffQuoteInput(long LoanId, string? AsOf = null);
