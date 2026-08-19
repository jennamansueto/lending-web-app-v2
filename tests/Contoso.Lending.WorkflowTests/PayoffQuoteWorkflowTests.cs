using Contoso.Lending.Workflow.Activities;
using Contoso.Lending.Workflow.Workflows;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using Xunit;

namespace Contoso.Lending.WorkflowTests;

public class PayoffQuoteWorkflowTests
{
    [Fact]
    public async Task PayoffQuoteIsReturnedExactlyAsTheServiceProducedIt()
    {
        var stub = new StubServiceApi().Enqueue(
            "/api/loans/5001/payoff?asOf=2026-08-19",
            """
            {"loanId":5001,"asOf":"2026-08-19","balance":383142.11,"accruedInterest":1512.44,
             "unpaidLateFees":75.00,"payoff":384729.55}
            """);

        await using var env = await WorkflowEnvironment.StartLocalAsync();
        var activities = new LendingServiceActivities(
            new HttpClient(stub) { BaseAddress = new Uri("http://service.test") });

        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions($"tq-{Guid.NewGuid():N}")
                .AddAllActivities(activities)
                .AddWorkflow<PayoffQuoteWorkflow>());

        var quote = await worker.ExecuteAsync(() => env.Client.ExecuteWorkflowAsync(
            (PayoffQuoteWorkflow wf) => wf.RunAsync(new PayoffQuoteInput(5001, "2026-08-19")),
            new WorkflowOptions($"wf-{Guid.NewGuid():N}", worker.Options.TaskQueue!)));

        Assert.Equal(5001, quote.LoanId);
        Assert.Equal("2026-08-19", quote.AsOf);
        Assert.Equal(383142.11m, quote.Balance);
        Assert.Equal(1512.44m, quote.AccruedInterest);
        Assert.Equal(75.00m, quote.UnpaidLateFees);
        Assert.Equal(384729.55m, quote.Payoff);
        Assert.Single(stub.Requests);
    }
}
