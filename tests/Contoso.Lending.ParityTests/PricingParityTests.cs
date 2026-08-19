using Contoso.Lending.Domain;

namespace Contoso.Lending.ParityTests;

public class PricingParityTests
{
    private const string Prc001 = "BR-PRC-001_base_rate.json";
    private const string Prc002 = "BR-PRC-002_risk_spread.json";
    private const string Prc003 = "BR-PRC-003_ltv_adjustment.json";
    private const string Prc004 = "BR-PRC-004_relationship_discount.json";
    private const string Prc005 = "BR-PRC-005_priced_rate.json";
    private const string Prc006 = "BR-PRC-006_origination_fee.json";
    private const string Prc007 = "BR-PRC-007_unknown_product_type.json";

    public static TheoryData<int, string> Cases_PRC_001 => GoldenCorpus.Cases(Prc001);
    public static TheoryData<int, string> Cases_PRC_002 => GoldenCorpus.Cases(Prc002);
    public static TheoryData<int, string> Cases_PRC_003 => GoldenCorpus.Cases(Prc003);
    public static TheoryData<int, string> Cases_PRC_004 => GoldenCorpus.Cases(Prc004);
    public static TheoryData<int, string> Cases_PRC_005 => GoldenCorpus.Cases(Prc005);
    public static TheoryData<int, string> Cases_PRC_006 => GoldenCorpus.Cases(Prc006);
    public static TheoryData<int, string> Cases_PRC_007 => GoldenCorpus.Cases(Prc007);

    [Theory]
    [MemberData(nameof(Cases_PRC_001))]
    public void BR_PRC_001(int index, string description)
    {
        _ = description;
        var record = GoldenCorpus.Record(Prc001, index);
        var input = record.GetProperty("input");
        Assert.Equal(record.GetProperty("expected").GetDecimal(),
            LoanCalculator.GetBaseRate(input.GetProperty("productType").GetString()!));
    }

    [Theory]
    [MemberData(nameof(Cases_PRC_002))]
    public void BR_PRC_002(int index, string description)
    {
        _ = description;
        var record = GoldenCorpus.Record(Prc002, index);
        var input = record.GetProperty("input");
        Assert.Equal(record.GetProperty("expected").GetDecimal(),
            LoanCalculator.GetRiskSpread(GoldenCorpus.GetInt(input, "creditScore")));
    }

    [Theory]
    [MemberData(nameof(Cases_PRC_003))]
    public void BR_PRC_003(int index, string description)
    {
        _ = description;
        var record = GoldenCorpus.Record(Prc003, index);
        var input = record.GetProperty("input");
        Assert.Equal(record.GetProperty("expected").GetDecimal(),
            LoanCalculator.GetLtvAdjustment(input.GetProperty("ltv").GetDecimal()));
    }

    [Theory]
    [MemberData(nameof(Cases_PRC_004))]
    public void BR_PRC_004(int index, string description)
    {
        _ = description;
        var record = GoldenCorpus.Record(Prc004, index);
        var input = record.GetProperty("input");
        Assert.Equal(record.GetProperty("expected").GetDecimal(),
            LoanCalculator.GetRelationshipDiscount(input.GetProperty("depositBalance").GetDecimal()));
    }

    [Theory]
    [MemberData(nameof(Cases_PRC_005))]
    public void BR_PRC_005(int index, string description)
    {
        _ = description;
        var record = GoldenCorpus.Record(Prc005, index);
        var input = record.GetProperty("input");
        Assert.Equal(record.GetProperty("expected").GetDecimal(),
            LoanCalculator.PriceRate(
                input.GetProperty("productType").GetString()!,
                GoldenCorpus.GetInt(input, "creditScore"),
                input.GetProperty("ltv").GetDecimal(),
                input.GetProperty("depositBalance").GetDecimal()));
    }

    [Theory]
    [MemberData(nameof(Cases_PRC_006))]
    public void BR_PRC_006(int index, string description)
    {
        _ = description;
        var record = GoldenCorpus.Record(Prc006, index);
        var input = record.GetProperty("input");
        Assert.Equal(record.GetProperty("expected").GetDecimal(),
            LoanCalculator.CalcOriginationFee(
                input.GetProperty("amount").GetDecimal(),
                input.GetProperty("productType").GetString()!));
    }

    [Theory]
    [MemberData(nameof(Cases_PRC_007))]
    public void BR_PRC_007(int index, string description)
    {
        _ = description;
        var record = GoldenCorpus.Record(Prc007, index);
        var input = record.GetProperty("input");
        var expectedError = record.GetProperty("expectedError");
        string entryPoint = input.GetProperty("entryPoint").GetString()!;
        string productType = input.GetProperty("productType").GetString()!;

        Exception ex = entryPoint switch
        {
            "LoanCalculator.GetBaseRate" =>
                Record.Exception(() => LoanCalculator.GetBaseRate(productType))!,
            "LoanCalculator.CalcOriginationFee" =>
                Record.Exception(() => LoanCalculator.CalcOriginationFee(
                    input.GetProperty("amount").GetDecimal(), productType))!,
            _ => throw new InvalidOperationException("Unknown entryPoint: " + entryPoint),
        };

        Assert.NotNull(ex);
        Assert.Equal(GoldenCorpus.GetString(expectedError, "type"), ex.GetType().FullName);
        Assert.Equal(GoldenCorpus.GetString(expectedError, "message"), ex.Message);
    }
}
