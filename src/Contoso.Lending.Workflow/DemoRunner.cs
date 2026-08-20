using System.Text.Json;
using System.Text.Json.Serialization;
using Contoso.Lending.Workflow.Contracts;
using Contoso.Lending.Workflow.Workflows;
using Temporalio.Client;

namespace Contoso.Lending.Workflow;

/// <summary>
/// Dev/demo driver: starts the workflows on a live Temporal server and compares the outcome with
/// the legacy expectations copied from the golden corpus. The comparison is exact string equality —
/// no normalization, no tolerance. This is verification code, not workflow code.
/// </summary>
public static class DemoRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var path = Cli.Option(args, "--scenarios")
            ?? Path.Combine(AppContext.BaseDirectory, "demo", "scenarios.json");
        var file = JsonSerializer.Deserialize<ScenarioFile>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"No scenarios in {path}");

        var client = await ConnectAsync().ConfigureAwait(false);
        var failures = 0;
        var artifact = new List<object>();
        foreach (var scenario in file.Scenarios)
        {
            Console.WriteLine($"=== scenario '{scenario.Name}' — {scenario.Description}");
            var input = new LoanApplicationInput(new EligibilityRequest(
                scenario.BorrowerId,
                scenario.Input.ProductType,
                scenario.Input.Amount,
                scenario.Input.TermMonths,
                scenario.Input.AnnualIncome,
                scenario.Input.MonthlyDebt,
                scenario.Input.CreditScore,
                scenario.Input.CollateralValue,
                scenario.Input.YearsInBusiness));

            var id = $"demo-{scenario.Name}-{Guid.NewGuid():N}";
            var result = await client.ExecuteWorkflowAsync(
                (LoanApplicationWorkflow wf) => wf.RunAsync(input),
                new WorkflowOptions(id, WorkerConfig.TaskQueue)).ConfigureAwait(false);

            Console.WriteLine($"workflow id      : {id}");
            Console.WriteLine($"status           : {result.Status}");
            Console.WriteLine($"appId            : {result.AppId?.ToString() ?? "(not persisted)"}");
            Console.WriteLine($"decision         : {result.Decision}");
            Console.WriteLine($"declineReason    : {Escape(result.DeclineReason)}");
            Console.WriteLine($"firedRuleId      : {result.FiredRuleId}");
            Console.WriteLine($"resultText (esc) : {Escape(result.ResultText)}");
            Console.WriteLine($"expected   (esc) : {Escape(scenario.Expected.ResultText)}");

            var assertions = new[]
            {
                Assertion("decision", scenario.Expected.Decision, result.Decision),
                Assertion("resultText", scenario.Expected.ResultText, result.ResultText),
                Assertion("declineReason", scenario.Expected.DeclineReason, result.DeclineReason),
                Assertion("firedRuleId", scenario.Expected.FiredRuleId, result.FiredRuleId),
            };
            failures += assertions.Count(x => x.Status == "fail");
            artifact.Add(new
            {
                name = scenario.Name,
                workflowId = id,
                status = assertions.All(x => x.Status == "pass") ? "pass" : "fail",
                assertions,
            });
            Console.WriteLine();
        }

        string artifactPath = Cli.Option(args, "--artifact")
            ?? Path.Combine(RepositoryRoot(), "parity", "artifacts", "l4-scenarios.json");
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        File.WriteAllText(
            artifactPath,
            JsonSerializer.Serialize(artifact, new JsonSerializerOptions(Json) { WriteIndented = true }) + Environment.NewLine);

        Console.WriteLine(failures == 0
            ? "PARITY OK — every asserted field matched the legacy expectation byte-for-byte."
            : $"PARITY FAILED — {failures} mismatch(es).");
        return failures == 0 ? 0 : 1;
    }

    public static async Task<int> RunPayoffAsync(string[] args)
    {
        var loanId = long.Parse(
            Cli.Option(args, "--loan-id") ?? throw new ArgumentException("--loan-id is required"),
            null);
        var asOf = Cli.Option(args, "--as-of");

        var client = await ConnectAsync().ConfigureAwait(false);
        var id = $"demo-payoff-{loanId}-{Guid.NewGuid():N}";
        var quote = await client.ExecuteWorkflowAsync(
            (PayoffQuoteWorkflow wf) => wf.RunAsync(new PayoffQuoteInput(loanId, asOf)),
            new WorkflowOptions(id, WorkerConfig.TaskQueue)).ConfigureAwait(false);

        Console.WriteLine($"workflow id      : {id}");
        Console.WriteLine(JsonSerializer.Serialize(quote, Json));
        return 0;
    }

    private static Task<TemporalClient> ConnectAsync()
    {
        Console.WriteLine($"temporal : {WorkerConfig.TemporalAddress} (ns {WorkerConfig.TemporalNamespace})");
        Console.WriteLine($"service  : {WorkerConfig.ServiceBaseUrl}");
        return TemporalClient.ConnectAsync(new(WorkerConfig.TemporalAddress)
        {
            Namespace = WorkerConfig.TemporalNamespace,
        });
    }

    private static AssertionResult Assertion(string field, string? expected, string? actual)
    {
        var ok = string.Equals(expected, actual, StringComparison.Ordinal);
        Console.WriteLine($"  [{(ok ? "OK  " : "FAIL")}] {field}");
        if (!ok)
        {
            Console.WriteLine($"         expected {Escape(expected)} actual {Escape(actual)}");
        }

        return new AssertionResult(field, expected, actual, ok ? "pass" : "fail");
    }

    private static string Escape(string? value) =>
        value is null ? "(null)" : JsonSerializer.Serialize(value);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "parity", "golden")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("could not locate repository root");
    }

    private sealed record AssertionResult(
        string Field,
        string? Expected,
        string? Actual,
        string Status);

    private sealed record ScenarioFile(List<Scenario> Scenarios);

    private sealed record Scenario(
        string Name,
        string Description,
        long BorrowerId,
        ScenarioInput Input,
        ScenarioExpected Expected);

    private sealed record ScenarioInput(
        string ProductType,
        decimal Amount,
        int TermMonths,
        decimal AnnualIncome,
        decimal MonthlyDebt,
        int CreditScore,
        decimal CollateralValue,
        int YearsInBusiness);

    private sealed record ScenarioExpected(
        string Decision,
        string ResultText,
        string? DeclineReason,
        string? FiredRuleId);
}
