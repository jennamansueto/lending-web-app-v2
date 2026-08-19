using Contoso.Lending.Domain;

namespace Contoso.Lending.ParityTests;

public class AmortizationParityTests
{
    private const string Amt001 = "BR-AMT-001_monthly_payment.json";
    private const string Amt002 = "BR-AMT-002_amortization_schedule.json";
    private const string Amt003 = "BR-AMT-003_non_positive_term.json";

    public static TheoryData<int, string> Cases_AMT_001 => GoldenCorpus.Cases(Amt001);
    public static TheoryData<int, string> Cases_AMT_002 => GoldenCorpus.Cases(Amt002);
    public static TheoryData<int, string> Cases_AMT_003 => GoldenCorpus.Cases(Amt003);

    [Theory]
    [MemberData(nameof(Cases_AMT_001))]
    public void BR_AMT_001(int index, string description)
    {
        _ = description;
        var record = GoldenCorpus.Record(Amt001, index);
        var input = record.GetProperty("input");
        Assert.Equal(record.GetProperty("expected").GetDecimal(),
            LoanCalculator.MonthlyPayment(
                input.GetProperty("principal").GetDecimal(),
                input.GetProperty("annualRatePct").GetDecimal(),
                GoldenCorpus.GetInt(input, "termMonths")));
    }

    [Theory]
    [MemberData(nameof(Cases_AMT_002))]
    public void BR_AMT_002(int index, string description)
    {
        _ = description;
        var record = GoldenCorpus.Record(Amt002, index);
        var input = record.GetProperty("input");
        var expectedRows = record.GetProperty("expected").EnumerateArray().ToArray();

        var rows = LoanCalculator.BuildSchedule(
            input.GetProperty("principal").GetDecimal(),
            input.GetProperty("annualRatePct").GetDecimal(),
            GoldenCorpus.GetInt(input, "termMonths"));

        Assert.Equal(expectedRows.Length, rows.Count);
        for (int r = 0; r < expectedRows.Length; r++)
        {
            Assert.Equal(GoldenCorpus.GetInt(expectedRows[r], "Period"), rows[r].Period);
            Assert.Equal(expectedRows[r].GetProperty("Payment").GetDecimal(), rows[r].Payment);
            Assert.Equal(expectedRows[r].GetProperty("Interest").GetDecimal(), rows[r].Interest);
            Assert.Equal(expectedRows[r].GetProperty("Principal").GetDecimal(), rows[r].Principal);
            Assert.Equal(expectedRows[r].GetProperty("Balance").GetDecimal(), rows[r].Balance);
        }
    }

    [Theory]
    [MemberData(nameof(Cases_AMT_003))]
    public void BR_AMT_003(int index, string description)
    {
        var record = GoldenCorpus.Record(Amt003, index);
        var input = record.GetProperty("input");
        var expectedError = record.GetProperty("expectedError");
        decimal principal = input.GetProperty("principal").GetDecimal();
        decimal annualRatePct = input.GetProperty("annualRatePct").GetDecimal();
        int termMonths = GoldenCorpus.GetInt(input, "termMonths");

        // The corpus exercises both entry points; the description names which one.
        Exception? ex = description.StartsWith("BuildSchedule", StringComparison.Ordinal)
            ? Record.Exception(() => LoanCalculator.BuildSchedule(principal, annualRatePct, termMonths))
            : Record.Exception(() => LoanCalculator.MonthlyPayment(principal, annualRatePct, termMonths));

        Assert.NotNull(ex);
        Assert.Equal(GoldenCorpus.GetString(expectedError, "type"), ex!.GetType().FullName);
        Assert.Equal(GoldenCorpus.GetString(expectedError, "message"), ex.Message);
    }
}
