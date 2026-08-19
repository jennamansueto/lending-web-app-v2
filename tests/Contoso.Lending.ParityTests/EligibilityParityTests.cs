using System.Text.Json;
using Contoso.Lending.Domain;

namespace Contoso.Lending.ParityTests;

public class EligibilityParityTests
{
    private const string Elg001 = "BR-ELG-001_amount_minimum.json";
    private const string Elg002 = "BR-ELG-002_amount_desk_maximum.json";
    private const string Elg003 = "BR-ELG-003_term_limits_by_product.json";
    private const string Elg004 = "BR-ELG-004_minimum_credit_score_by_product.json";
    private const string Elg005 = "BR-ELG-005_dti_cap.json";
    private const string Elg006 = "BR-ELG-006_collateral_value_required.json";
    private const string Elg007 = "BR-ELG-007_ltv_cap_by_product.json";
    private const string Elg008 = "BR-ELG-008_loc_minimum_years_in_business.json";
    private const string Elg009 = "BR-ELG-009_eligibility_decision.json";

    public static TheoryData<int, string> Cases_ELG_001 => GoldenCorpus.Cases(Elg001);
    public static TheoryData<int, string> Cases_ELG_002 => GoldenCorpus.Cases(Elg002);
    public static TheoryData<int, string> Cases_ELG_003 => GoldenCorpus.Cases(Elg003);
    public static TheoryData<int, string> Cases_ELG_004 => GoldenCorpus.Cases(Elg004);
    public static TheoryData<int, string> Cases_ELG_005 => GoldenCorpus.Cases(Elg005);
    public static TheoryData<int, string> Cases_ELG_006 => GoldenCorpus.Cases(Elg006);
    public static TheoryData<int, string> Cases_ELG_007 => GoldenCorpus.Cases(Elg007);
    public static TheoryData<int, string> Cases_ELG_008 => GoldenCorpus.Cases(Elg008);
    public static TheoryData<int, string> Cases_ELG_009 => GoldenCorpus.Cases(Elg009);

    [Theory]
    [MemberData(nameof(Cases_ELG_001))]
    public void BR_ELG_001(int index, string description) => AssertRecord(Elg001, index, description);

    [Theory]
    [MemberData(nameof(Cases_ELG_002))]
    public void BR_ELG_002(int index, string description) => AssertRecord(Elg002, index, description);

    [Theory]
    [MemberData(nameof(Cases_ELG_003))]
    public void BR_ELG_003(int index, string description) => AssertRecord(Elg003, index, description);

    [Theory]
    [MemberData(nameof(Cases_ELG_004))]
    public void BR_ELG_004(int index, string description) => AssertRecord(Elg004, index, description);

    [Theory]
    [MemberData(nameof(Cases_ELG_005))]
    public void BR_ELG_005(int index, string description) => AssertRecord(Elg005, index, description);

    [Theory]
    [MemberData(nameof(Cases_ELG_006))]
    public void BR_ELG_006(int index, string description) => AssertRecord(Elg006, index, description);

    [Theory]
    [MemberData(nameof(Cases_ELG_007))]
    public void BR_ELG_007(int index, string description) => AssertRecord(Elg007, index, description);

    [Theory]
    [MemberData(nameof(Cases_ELG_008))]
    public void BR_ELG_008(int index, string description) => AssertRecord(Elg008, index, description);

    [Theory]
    [MemberData(nameof(Cases_ELG_009))]
    public void BR_ELG_009(int index, string description) => AssertRecord(Elg009, index, description);

    internal static EligibilityInput ReadInput(JsonElement input) => new()
    {
        Product = input.GetProperty("product").GetString()!,
        Amount = input.GetProperty("amount").GetDecimal(),
        TermMonths = GoldenCorpus.GetInt(input, "termMonths"),
        AnnualIncome = input.GetProperty("annualIncome").GetDecimal(),
        MonthlyDebt = input.GetProperty("monthlyDebt").GetDecimal(),
        CreditScore = GoldenCorpus.GetInt(input, "creditScore"),
        CollateralValue = input.GetProperty("collateralValue").GetDecimal(),
        YearsInBusiness = GoldenCorpus.GetInt(input, "yearsInBusiness"),
    };

    private static void AssertRecord(string file, int index, string description)
    {
        _ = description;
        var record = GoldenCorpus.Record(file, index);
        var expected = record.GetProperty("expected");

        var result = EligibilityEngine.Evaluate(ReadInput(record.GetProperty("input")));

        Assert.Equal(GoldenCorpus.GetString(expected, "decision"), result.Decision);
        Assert.Equal(GoldenCorpus.GetString(expected, "resultText"), result.ResultText);
        Assert.Equal(GoldenCorpus.GetString(expected, "declineReason"), result.DeclineReason);
        Assert.Equal(GoldenCorpus.GetString(expected, "firedRuleId"), result.FiredRuleId);
        Assert.Equal(GoldenCorpus.GetDecimal(expected, "dti"), result.Dti);
        Assert.Equal(GoldenCorpus.GetString(expected, "dtiDisplay"), result.DtiDisplay);
        Assert.Equal(GoldenCorpus.GetDecimal(expected, "ltv"), result.Ltv);
        Assert.Equal(GoldenCorpus.GetString(expected, "ltvDisplay"), result.LtvDisplay);
        Assert.Equal(GoldenCorpus.GetDecimal(expected, "estPayment"), result.EstPayment);
        Assert.Equal(GoldenCorpus.GetString(expected, "estPaymentDisplay"), result.EstPaymentDisplay);
        Assert.Equal(GoldenCorpus.GetString(expected, "resultLabelColor"), result.ResultLabelColor);
    }
}
