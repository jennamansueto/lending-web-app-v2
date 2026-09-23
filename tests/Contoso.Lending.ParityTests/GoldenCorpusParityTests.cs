using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Contoso.Lending.Domain;
using Xunit;

namespace Contoso.Lending.ParityTests;

/// <summary>
/// One test case per golden record: exact equality, no tolerance. A failure names the rule ID
/// and the record index inside its golden file.
/// </summary>
public class GoldenCorpusParityTests
{
    public static IEnumerable<object[]> AllRecords =>
        GoldenCorpus.RuleFiles.Keys
            .OrderBy(ruleId => ruleId, StringComparer.Ordinal)
            .SelectMany(ruleId => Enumerable
                .Range(0, GoldenCorpus.Records(ruleId).Count)
                .Select(index => new object[] { ruleId, index }));

    [Theory]
    [MemberData(nameof(AllRecords))]
    public void Record_matches_legacy(string ruleId, int index)
    {
        JsonElement record = GoldenCorpus.Records(ruleId)[index];
        Assert.Equal(ruleId, record.GetProperty("ruleId").GetString());

        JsonElement input = record.GetProperty("input");
        JsonElement expected = record.GetProperty("expected");

        switch (ruleId)
        {
            case "BR-PRC-001":
                Assert.Equal(expected.GetDecimal(), PricingEngine.GetBaseRate(Str(input, "productType")));
                break;
            case "BR-PRC-002":
                Assert.Equal(expected.GetDecimal(), PricingEngine.GetRiskSpread(Int(input, "creditScore")));
                break;
            case "BR-PRC-003":
                Assert.Equal(expected.GetDecimal(), PricingEngine.GetLtvAdjustment(Dec(input, "ltv")));
                break;
            case "BR-PRC-004":
                Assert.Equal(expected.GetDecimal(), PricingEngine.GetRelationshipDiscount(Dec(input, "depositBalance")));
                break;
            case "BR-PRC-005":
                Assert.Equal(expected.GetDecimal(), PricingEngine.PriceRate(
                    Str(input, "productType"), Int(input, "creditScore"), Dec(input, "ltv"), Dec(input, "depositBalance")));
                break;
            case "BR-PRC-006":
                Assert.Equal(expected.GetDecimal(), PricingEngine.CalcOriginationFee(
                    Dec(input, "amount"), Str(input, "productType")));
                break;
            case "BR-AMT-001":
                Assert.Equal(expected.GetDecimal(), AmortizationEngine.MonthlyPayment(
                    Dec(input, "principal"), Dec(input, "annualRatePct"), Int(input, "termMonths")));
                break;
            case "BR-AMT-002":
                AssertSchedule(expected, AmortizationEngine.BuildSchedule(
                    Dec(input, "principal"), Dec(input, "annualRatePct"), Int(input, "termMonths")));
                break;
            case "BR-PQL-001":
                AssertPrequalification(expected, PreQualificationEngine.Evaluate(Int(input, "creditScore")));
                break;
            case "BR-SVC-001":
                Assert.Equal(expected.GetProperty("lateFee").GetDecimal(), ServicingEngine.CalcLateFee(
                    Dec(input, "paymentAmount"), Int(input, "daysLate")));
                break;
            case "BR-SVC-002":
                Assert.Equal(
                    expected.GetProperty("payoffAmount").GetDecimal(),
                    ServicingEngine.CalcPayoff(
                        LegacySeededDatabase.Loan(Int(input, "loanId")),
                        DateOnly.ParseExact(Str(input, "asOf"), "yyyy-MM-dd", CultureInfo.InvariantCulture)).Payoff);
                break;
            default:
                Assert.StartsWith("BR-ELG-", ruleId);
                AssertEligibility(record, expected, EligibilityEngine.Evaluate(new EligibilityRequest(
                    Str(input, "productType"),
                    Dec(input, "amount"),
                    Int(input, "termMonths"),
                    Dec(input, "annualIncome"),
                    Dec(input, "monthlyDebt"),
                    Int(input, "creditScore"),
                    Dec(input, "collateralValue"),
                    Int(input, "yearsInBusiness"))));
                break;
        }
    }

    private static void AssertEligibility(JsonElement record, JsonElement expected, EligibilityResult actual)
    {
        // Every eligibility record was captured under en-US; the engine pins that culture itself.
        Assert.Equal("en-US", record.GetProperty("culture").GetString());

        Assert.Equal(expected.GetProperty("decision").GetString(), actual.Decision);
        Assert.Equal(NullableString(expected, "declineReason"), actual.DeclineReason);
        // BR-ELG-010: first failing rule wins, in legacy source order.
        Assert.Equal(expected.GetProperty("firedRuleId").GetString(), actual.FiredRuleId);
        Assert.Equal(NullableDecimal(expected, "dti"), actual.Dti);
        Assert.Equal(NullableDecimal(expected, "ltv"), actual.Ltv);
        Assert.Equal(NullableDecimal(expected, "estimatedPayment"), actual.EstimatedPayment);
        Assert.Equal(expected.GetProperty("resultText").GetString(), actual.ResultText);
    }

    private static void AssertPrequalification(JsonElement expected, PreQualificationResult actual)
    {
        Assert.Equal(expected.GetProperty("prequalifiedProducts").GetString(), actual.PrequalifiedProducts);
        Assert.Equal(expected.GetProperty("labelText").GetString(), actual.ResultText);
    }

    private static void AssertSchedule(JsonElement expected, IReadOnlyList<AmortizationRow> actual)
    {
        var expectedRows = expected.EnumerateArray().ToList();
        Assert.Equal(expectedRows.Count, actual.Count);
        for (int row = 0; row < expectedRows.Count; row++)
        {
            Assert.Equal(expectedRows[row].GetProperty("Period").GetInt32(), actual[row].Period);
            Assert.Equal(expectedRows[row].GetProperty("Payment").GetDecimal(), actual[row].Payment);
            Assert.Equal(expectedRows[row].GetProperty("Interest").GetDecimal(), actual[row].Interest);
            Assert.Equal(expectedRows[row].GetProperty("Principal").GetDecimal(), actual[row].Principal);
            Assert.Equal(expectedRows[row].GetProperty("Balance").GetDecimal(), actual[row].Balance);
        }
    }

    private static string Str(JsonElement element, string name) => element.GetProperty(name).GetString()!;
    private static int Int(JsonElement element, string name) => element.GetProperty(name).GetInt32();
    private static decimal Dec(JsonElement element, string name) => element.GetProperty(name).GetDecimal();

    private static string? NullableString(JsonElement element, string name) =>
        element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : element.GetProperty(name).GetString();

    private static decimal? NullableDecimal(JsonElement element, string name) =>
        element.GetProperty(name).ValueKind == JsonValueKind.Null ? null : element.GetProperty(name).GetDecimal();
}
