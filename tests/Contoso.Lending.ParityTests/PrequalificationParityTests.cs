using Contoso.Lending.Domain;

namespace Contoso.Lending.ParityTests;

public class PrequalificationParityTests
{
    private const string Pql001 = "BR-PQL-001_prequalification_hint.json";

    public static TheoryData<int, string> Cases_PQL_001 => GoldenCorpus.Cases(Pql001);

    [Theory]
    [MemberData(nameof(Cases_PQL_001))]
    public void BR_PQL_001(int index, string description)
    {
        _ = description;
        var record = GoldenCorpus.Record(Pql001, index);
        var expected = record.GetProperty("expected");

        var result = PrequalificationEngine.Evaluate(
            GoldenCorpus.GetInt(record.GetProperty("input"), "creditScore"));

        Assert.Equal(GoldenCorpus.GetString(expected, "prequalifiedProducts"), result.PrequalifiedProducts);
        Assert.Equal(GoldenCorpus.GetString(expected, "labelText"), result.LabelText);
    }
}
