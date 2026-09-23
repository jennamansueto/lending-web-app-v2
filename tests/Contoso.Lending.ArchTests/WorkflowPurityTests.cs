namespace Contoso.Lending.ArchTests;

/// <summary>
/// The source-level half of the purity gate: no calculation, threshold or rounding may appear in
/// the workflow sources, naming the offending file and line when one does.
/// </summary>
public class WorkflowPurityTests
{
    [Fact]
    public void WorkflowSources_ContainNoBusinessLogic()
    {
        var violations = WorkflowSourceScanner.Scan(RepoPaths.WorkflowProjectDir);

        Assert.True(
            violations.Count == 0,
            $"layer 3 must contain no business logic; {violations.Count} violation(s) found:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    [Theory]
    // Every line below is a business rule someone might "just add" to the workflow. The scanner
    // must reject each one, which is what makes the test above meaningful.
    [InlineData("decimal fee = input.Amount * 0.01m;", "money-arithmetic")]
    [InlineData("decimal fee = input.Amount * 0.01m;", "decimal-literal")]
    [InlineData("if (input.CreditScore < 640) { return Declined(eligibility); }", "threshold-comparison")]
    [InlineData("if (quote.Rate.Rate >= ceiling) { rate = ceiling; }", "threshold-comparison")]
    [InlineData("decimal rate = quote.Rate.BaseRate + spread;", "money-arithmetic")]
    [InlineData("var payment = Math.Round(quote.Payment, 2, MidpointRounding.AwayFromZero);", "rounding")]
    [InlineData("decimal maxLtv = 0.90;", "fractional-literal")]
    [InlineData("using Contoso.Lending.Domain;", "forbidden-reference")]
    [InlineData("await using var connection = new NpgsqlConnection(conn);", "forbidden-reference")]
    public void Scanner_RejectsBusinessLogic(string line, string expectedRule)
    {
        var violations = WorkflowSourceScanner.ScanLine("Injected.cs", 42, line);

        Assert.Contains(violations, violation => violation.Rule == expectedRule);
    }

    [Theory]
    // Orchestration constructs the scanner must leave alone, so the gate stays usable.
    [InlineData("StartToCloseTimeout = TimeSpan.FromSeconds(30),")]
    [InlineData("if (eligibility.Decision != Decisions.Approved)")]
    [InlineData("private readonly List<string> reviewers = new();")]
    [InlineData("public Task<QuoteResponse> PriceLoanAsync(QuoteRequest request) =>")]
    [InlineData("// the fee is 1% of the amount above 0.90 ltv, rounded away from zero")]
    [InlineData("client.PostAsync<QuoteRequest, QuoteResponse>(\"/api/pricing/quote\", request, ct);")]
    public void Scanner_AllowsOrchestration(string line)
    {
        var violations = WorkflowSourceScanner.ScanLine("Injected.cs", 42, line);

        Assert.Empty(violations);
    }
}
