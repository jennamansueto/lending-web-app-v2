using System.Net;
using Contoso.Lending.Workflow.Activities;
using Contoso.Lending.Workflow.Contracts;
using Contoso.Lending.Workflow.Workflows;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;
using Xunit;

namespace Contoso.Lending.WorkflowTests;

public class LoanApplicationWorkflowTests
{
    // Bodies below are the layer-2 responses for the golden corpus cases
    // parity/golden/BR-ELG-009_eligibility_decision.json (legacy repo):
    // "approval: TERM typical application" and "precedence 4/9: term repaired -> credit score fires".
    private const string ApprovedEligibilityJson = """
        {"decision":"APPROVED","declineReason":null,"dti":0.1391322,"ltv":0.5,
         "estimatedPayment":1956.61,
         "resultText":"APPROVED FOR UNDERWRITING\nDTI: 0.139   LTV: 0.500\nEst. payment at base rate: $1,956.61",
         "firedRuleId":"BR-ELG-009","ruleIds":["BR-ELG-001","BR-ELG-009"]}
        """;

    private const string DeclinedEligibilityJson = """
        {"decision":"DECLINED","declineReason":"Credit score 500 below product minimum of 660.",
         "dti":null,"ltv":null,"estimatedPayment":null,
         "resultText":"DECLINED\nCredit score 500 below product minimum of 660.",
         "firedRuleId":"BR-ELG-004","ruleIds":["BR-ELG-001","BR-ELG-004"]}
        """;

    private static readonly EligibilityRequest ApprovedIntake = new(
        BorrowerId: 1, ProductType: "TERM", Amount: 100000m, TermMonths: 60,
        AnnualIncome: 600000m, MonthlyDebt: 5000m, CreditScore: 700,
        CollateralValue: 200000m, YearsInBusiness: 5);

    private static readonly EligibilityRequest DeclinedIntake = new(
        BorrowerId: 1, ProductType: "LOC", Amount: 25000m, TermMonths: 24,
        AnnualIncome: 12000m, MonthlyDebt: 5000m, CreditScore: 500,
        CollateralValue: -100m, YearsInBusiness: 0);

    [Fact]
    public async Task ApprovedApplicationPersistsAndHandsOffWithTheServiceResultText()
    {
        var stub = new StubServiceApi()
            .Enqueue("/api/eligibility/evaluate", ApprovedEligibilityJson)
            .Enqueue("/api/applications", """{"appId":1000}""");

        var result = await RunAsync(stub, new LoanApplicationInput(ApprovedIntake));

        Assert.Equal("APPROVED", result.Decision);
        Assert.Equal(
            "APPROVED FOR UNDERWRITING\nDTI: 0.139   LTV: 0.500\nEst. payment at base rate: $1,956.61",
            result.ResultText);
        Assert.Equal(0.1391322m, result.Dti);
        Assert.Equal(0.5m, result.Ltv);
        Assert.Equal(1956.61m, result.EstimatedPayment);
        Assert.Equal(1000, result.AppId);
        Assert.Equal("AWAITING_UNDERWRITING", result.Status);
        Assert.Null(result.DeclineReason);
        Assert.Collection(
            stub.Requests,
            first => Assert.Equal("/api/eligibility/evaluate", first.Path),
            second => Assert.Equal("/api/applications", second.Path));
    }

    [Fact]
    public async Task DeclinedApplicationIsNotPersistedAndCarriesTheLegacyDeclineText()
    {
        var stub = new StubServiceApi()
            .Enqueue("/api/eligibility/evaluate", DeclinedEligibilityJson);

        var result = await RunAsync(stub, new LoanApplicationInput(DeclinedIntake));

        Assert.Equal("DECLINED", result.Decision);
        Assert.Equal("DECLINED\nCredit score 500 below product minimum of 660.", result.ResultText);
        Assert.Equal("Credit score 500 below product minimum of 660.", result.DeclineReason);
        Assert.Equal("BR-ELG-004", result.FiredRuleId);
        Assert.Null(result.AppId);
        Assert.Equal("DECLINED", result.Status);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task UnderwritingBookSignalBooksTheLoanWithServiceSuppliedNumbers()
    {
        var stub = new StubServiceApi()
            .Enqueue("/api/eligibility/evaluate", ApprovedEligibilityJson)
            .Enqueue("/api/applications", """{"appId":1000}""")
            .Enqueue(
                "/api/pricing/quote",
                """
                {"rate":6.85,"originationFee":1000.00,"monthlyPayment":1972.53,
                 "rateLabel":"6.85%","feeLabel":"$1,000.00","paymentLabel":"$1,972.53","rows":[]}
                """)
            .Enqueue("/api/loans", """{"loanId":5010}""");

        var input = new LoanApplicationInput(
            ApprovedIntake,
            DepositBalance: 0m,
            FundedDate: "2026-08-19",
            UnderwritingDecisionTimeout: TimeSpan.FromMinutes(5));

        var result = await RunAsync(stub, input, async handle =>
            await handle.SignalAsync(wf =>
                wf.SubmitUnderwritingDecision(new UnderwritingDecisionSignal("BOOK", "credit ok"))));

        Assert.Equal("BOOKED", result.Status);
        Assert.Equal(5010, result.LoanId);
        Assert.Equal(6.85m, result.Rate);
        Assert.Equal(1000.00m, result.OriginationFee);
        Assert.Equal(1972.53m, result.Payment);
        Assert.Equal(
            new[] { "/api/eligibility/evaluate", "/api/applications", "/api/pricing/quote", "/api/loans" },
            stub.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task ServiceOutageIsRetriedUntilTheServiceAnswers()
    {
        var stub = new StubServiceApi()
            .Enqueue("/api/eligibility/evaluate", """{"message":"boom"}""", HttpStatusCode.InternalServerError)
            .Enqueue("/api/eligibility/evaluate", DeclinedEligibilityJson);

        var result = await RunAsync(stub, new LoanApplicationInput(DeclinedIntake));

        Assert.Equal("DECLINED\nCredit score 500 below product minimum of 660.", result.ResultText);
        Assert.Equal(2, stub.Requests.Count);
    }

    private static async Task<LoanApplicationResult> RunAsync(
        StubServiceApi stub,
        LoanApplicationInput input,
        Func<WorkflowHandle<LoanApplicationWorkflow, LoanApplicationResult>, Task>? afterStart = null)
    {
        await using var env = await WorkflowEnvironment.StartLocalAsync();
        var activities = new LendingServiceActivities(
            new HttpClient(stub) { BaseAddress = new Uri("http://service.test") });

        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions($"tq-{Guid.NewGuid():N}")
                .AddAllActivities(activities)
                .AddWorkflow<LoanApplicationWorkflow>());

        return await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                (LoanApplicationWorkflow wf) => wf.RunAsync(input),
                new WorkflowOptions($"wf-{Guid.NewGuid():N}", worker.Options.TaskQueue!));
            if (afterStart is not null)
            {
                await afterStart(handle);
            }

            return await handle.GetResultAsync();
        });
    }
}
