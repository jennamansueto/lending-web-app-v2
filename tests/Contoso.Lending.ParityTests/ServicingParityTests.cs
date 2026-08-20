using System.Globalization;
using Contoso.Lending.Domain;

namespace Contoso.Lending.ParityTests;

[Collection(ParityArtifactCollection.Name)]
public class ServicingParityTests
{
    private const string Svc001 = "BR-SVC-001_late_fee.json";
    private const string Svc002 = "BR-SVC-002_payoff_quote.json";

    public static TheoryData<int, string> Cases_SVC_001 => GoldenCorpus.Cases(Svc001);
    public static TheoryData<int, string> Cases_SVC_002 => GoldenCorpus.Cases(Svc002);

    [Theory]
    [MemberData(nameof(Cases_SVC_001))]
    public void BR_SVC_001(int index, string description)
    {
        _ = description;
        var record = GoldenCorpus.Record(Svc001, index);
        var input = record.GetProperty("input");
        var expected = record.GetProperty("expected");

        decimal fee = ServicingCalculator.CalcLateFee(
            input.GetProperty("paymentAmount").GetDecimal(),
            GoldenCorpus.GetInt(input, "daysLate"));

        ParityArtifact.Complete(Svc001, index, new
        {
            lateFee = fee,
            labelText = ServicingCalculator.LateFeeLabel(fee),
        });

        Assert.Equal(expected.GetProperty("lateFee").GetDecimal(), fee);
        Assert.Equal(GoldenCorpus.GetString(expected, "labelText"), ServicingCalculator.LateFeeLabel(fee));
    }

    [Theory]
    [MemberData(nameof(Cases_SVC_002))]
    public void BR_SVC_002(int index, string description)
    {
        _ = description;
        var record = GoldenCorpus.Record(Svc002, index);
        var input = record.GetProperty("input");
        var expected = record.GetProperty("expected");

        // dbContext is the database state GET_PAYOFF_AMOUNT saw when the golden
        // record was executed; the test re-derives the payoff from it.
        var db = record.GetProperty("dbContext");
        DateTime asOf = DateTime.ParseExact(GoldenCorpus.GetString(input, "asOfDate")!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        DateTime fundedDate = DateTime.ParseExact(GoldenCorpus.GetString(db, "fundedDate")!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        decimal principal = db.GetProperty("principal").GetDecimal();
        decimal annualRate = db.GetProperty("annualRate").GetDecimal();
        decimal lateFees = db.GetProperty("unpaidLateFees").GetDecimal();
        bool fallback = db.GetProperty("usedFundedDateFallback").GetBoolean();

        IReadOnlyList<ScheduleEntry> schedule = fallback
            ? Array.Empty<ScheduleEntry>()
            : new[]
            {
                new ScheduleEntry(
                    DateTime.ParseExact(GoldenCorpus.GetString(db, "accrualFromDate")!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    db.GetProperty("balanceBasis").GetDecimal()),
            };

        decimal payoff = ServicingCalculator.GetPayoffAmount(annualRate, principal, fundedDate, schedule, lateFees, asOf);
        ParityArtifact.Complete(Svc002, index, new { payoffAmount = payoff });
        Assert.Equal(expected.GetProperty("payoffAmount").GetDecimal(), payoff);
    }
}
