using System;

namespace Contoso.Lending.Domain;

/// <summary>
/// Pricing rules extracted 1:1 from legacy `src/LendingDesk/Core/LoanCalculator.cs`.
/// Rounding behavior is load-bearing (legacy ticket LEND-4471): decimal arithmetic with
/// <see cref="MidpointRounding.AwayFromZero"/>.
/// </summary>
public static class PricingEngine
{
    public const decimal RateFloor = 4.00m;
    public const decimal RateCeiling = 12.50m;

    // BR-PRC-001: base rate by product (TERM 6.50 / LOC 7.25 / EQUIP 6.90).
    public static decimal GetBaseRate(string productType) => productType switch
    {
        "TERM" => 6.50m,
        "LOC" => 7.25m,
        "EQUIP" => 6.90m,
        // BR-PRC-007: unknown product type throws with this exact message.
        _ => throw new ArgumentException("Unknown product type: " + productType)
    };

    // BR-PRC-002: risk spread by credit score band.
    public static decimal GetRiskSpread(int creditScore)
    {
        if (creditScore >= 760) return 0.00m;
        if (creditScore >= 720) return 0.35m;
        if (creditScore >= 680) return 0.85m;
        if (creditScore >= 640) return 1.60m;
        return 2.75m;
    }

    // BR-PRC-003: LTV rate adjustment.
    public static decimal GetLtvAdjustment(decimal ltv)
    {
        if (ltv <= 0.60m) return -0.15m;
        if (ltv <= 0.75m) return 0.00m;
        if (ltv <= 0.85m) return 0.40m;
        return 0.90m;
    }

    // BR-PRC-004: deposit relationship discount.
    public static decimal GetRelationshipDiscount(decimal depositBalance)
    {
        if (depositBalance >= 250000m) return 0.25m;
        if (depositBalance >= 100000m) return 0.10m;
        return 0.00m;
    }

    // BR-PRC-005: priced rate = base + spread + ltv adjustment - discount, clamped to
    // [4.00, 12.50] and rounded to 2dp half away from zero.
    public static decimal PriceRate(string productType, int creditScore, decimal ltv, decimal depositBalance)
    {
        decimal rate = GetBaseRate(productType)
                       + GetRiskSpread(creditScore)
                       + GetLtvAdjustment(ltv)
                       - GetRelationshipDiscount(depositBalance);
        if (rate < RateFloor) rate = RateFloor;
        if (rate > RateCeiling) rate = RateCeiling;
        return Math.Round(rate, 2, MidpointRounding.AwayFromZero);
    }

    public static PricedRate PriceRateDetailed(string productType, int creditScore, decimal ltv, decimal depositBalance)
    {
        decimal baseRate = GetBaseRate(productType);
        decimal spread = GetRiskSpread(creditScore);
        decimal ltvAdj = GetLtvAdjustment(ltv);
        decimal discount = GetRelationshipDiscount(depositBalance);
        decimal raw = baseRate + spread + ltvAdj - discount;
        bool floored = raw < RateFloor;
        bool capped = raw > RateCeiling;
        return new PricedRate(
            baseRate,
            spread,
            ltvAdj,
            discount,
            PriceRate(productType, creditScore, ltv, depositBalance),
            floored,
            capped);
    }

    // BR-PRC-006: origination fee by product, with per-product floor and (TERM only) cap.
    public static decimal CalcOriginationFee(decimal amount, string productType)
    {
        decimal fee;
        switch (productType)
        {
            case "TERM":
                fee = Math.Round(amount * 0.0100m, 2, MidpointRounding.AwayFromZero);
                if (fee < 500m) fee = 500m;
                if (fee > 25000m) fee = 25000m;
                break;
            case "LOC":
                fee = Math.Round(amount * 0.0075m, 2, MidpointRounding.AwayFromZero);
                if (fee < 350m) fee = 350m;
                break;
            case "EQUIP":
                fee = Math.Round(amount * 0.0125m, 2, MidpointRounding.AwayFromZero);
                if (fee < 500m) fee = 500m;
                break;
            default:
                // BR-PRC-007
                throw new ArgumentException("Unknown product type: " + productType);
        }
        return fee;
    }

    public static OriginationFee CalcOriginationFeeDetailed(decimal amount, string productType)
    {
        decimal fee = CalcOriginationFee(amount, productType);
        decimal raw = productType switch
        {
            "TERM" => Math.Round(amount * 0.0100m, 2, MidpointRounding.AwayFromZero),
            "LOC" => Math.Round(amount * 0.0075m, 2, MidpointRounding.AwayFromZero),
            "EQUIP" => Math.Round(amount * 0.0125m, 2, MidpointRounding.AwayFromZero),
            _ => throw new ArgumentException("Unknown product type: " + productType)
        };
        bool capApplied = productType == "TERM" && raw > 25000m;
        bool minApplied = !capApplied && raw < (productType == "LOC" ? 350m : 500m);
        return new OriginationFee(fee, minApplied, capApplied);
    }
}

public readonly record struct PricedRate(
    decimal BaseRate,
    decimal RiskSpread,
    decimal LtvAdjustment,
    decimal RelationshipDiscount,
    decimal Rate,
    bool Floored,
    bool Capped);

public readonly record struct OriginationFee(decimal Fee, bool MinApplied, bool CapApplied);
