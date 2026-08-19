namespace Contoso.Lending.Domain;

// BR-PRC-005 LoanCalculator.cs:52-61 — same arithmetic as LoanCalculator.PriceRate,
// exposing the components and clamp flags the pricing/rate endpoint reports.
public sealed class RateBreakdown
{
    public decimal BaseRate { get; init; }
    public decimal RiskSpread { get; init; }
    public decimal LtvAdjustment { get; init; }
    public decimal RelationshipDiscount { get; init; }
    public decimal Rate { get; init; }
    public bool Floored { get; init; }
    public bool Capped { get; init; }
}

// BR-PRC-006 LoanCalculator.cs:63-85 — fee with minimum/cap flags for the endpoint.
public sealed class FeeBreakdown
{
    public decimal Fee { get; init; }
    public bool MinApplied { get; init; }
    public bool CapApplied { get; init; }
}

public static class PricingBreakdown
{
    public static RateBreakdown Rate(string productType, int creditScore, decimal ltv, decimal depositBalance)
    {
        // BR-PRC-001..004 LoanCalculator.cs:17-50 — components.
        decimal baseRate = LoanCalculator.GetBaseRate(productType);
        decimal riskSpread = LoanCalculator.GetRiskSpread(creditScore);
        decimal ltvAdjustment = LoanCalculator.GetLtvAdjustment(ltv);
        decimal relationshipDiscount = LoanCalculator.GetRelationshipDiscount(depositBalance);

        // BR-PRC-005 LoanCalculator.cs:52-61 — floor before ceiling, then round.
        decimal rate = baseRate + riskSpread + ltvAdjustment - relationshipDiscount;
        bool floored = rate < LoanCalculator.RateFloor;
        if (floored) rate = LoanCalculator.RateFloor;
        bool capped = rate > LoanCalculator.RateCeiling;
        if (capped) rate = LoanCalculator.RateCeiling;

        return new RateBreakdown
        {
            BaseRate = baseRate,
            RiskSpread = riskSpread,
            LtvAdjustment = ltvAdjustment,
            RelationshipDiscount = relationshipDiscount,
            Rate = Math.Round(rate, 2, MidpointRounding.AwayFromZero),
            Floored = floored,
            Capped = capped,
        };
    }

    public static FeeBreakdown OriginationFee(decimal amount, string productType)
    {
        // BR-PRC-006 LoanCalculator.cs:63-85 — recompute the raw fee to report
        // whether the minimum or (TERM-only) cap bound the result.
        decimal fee = LoanCalculator.CalcOriginationFee(amount, productType);
        decimal raw = productType switch
        {
            "TERM" => Math.Round(amount * 0.0100m, 2, MidpointRounding.AwayFromZero),
            "LOC" => Math.Round(amount * 0.0075m, 2, MidpointRounding.AwayFromZero),
            "EQUIP" => Math.Round(amount * 0.0125m, 2, MidpointRounding.AwayFromZero),
            _ => throw new ArgumentException("Unknown product type: " + productType),
        };

        return new FeeBreakdown
        {
            Fee = fee,
            MinApplied = raw < fee,
            CapApplied = raw > fee,
        };
    }
}
