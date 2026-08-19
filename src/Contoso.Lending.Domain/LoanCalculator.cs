namespace Contoso.Lending.Domain;

/// <summary>
/// 1:1 port of legacy Core/LoanCalculator.cs (pricing and amortization).
/// Rounding behavior is frozen by Finance sign-off (ticket LEND-4471) — do not
/// change any rounding mode, clamp order, or the double round-trip in
/// MonthlyPayment.
/// </summary>
public static class LoanCalculator
{
    // BR-PRC-005 LoanCalculator.cs:13-14
    public const decimal RateFloor = 4.00m;
    public const decimal RateCeiling = 12.50m;

    // BR-PRC-001 LoanCalculator.cs:17-26 — base rate by product; exact,
    // case-sensitive match. BR-PRC-007 LoanCalculator.cs:24 — unknown product rejection.
    public static decimal GetBaseRate(string productType)
    {
        switch (productType)
        {
            case "TERM": return 6.50m;
            case "LOC": return 7.25m;
            case "EQUIP": return 6.90m;
            default: throw new ArgumentException("Unknown product type: " + productType);
        }
    }

    // BR-PRC-002 LoanCalculator.cs:28-35 — risk spread bands, top-down, >= inclusive.
    public static decimal GetRiskSpread(int creditScore)
    {
        if (creditScore >= 760) return 0.00m;
        if (creditScore >= 720) return 0.35m;
        if (creditScore >= 680) return 0.85m;
        if (creditScore >= 640) return 1.60m;
        return 2.75m;
    }

    // BR-PRC-003 LoanCalculator.cs:37-43 — LTV adjustment bands, top-down, <= inclusive.
    public static decimal GetLtvAdjustment(decimal ltv)
    {
        if (ltv <= 0.60m) return -0.15m;
        if (ltv <= 0.75m) return 0.00m;
        if (ltv <= 0.85m) return 0.40m;
        return 0.90m;
    }

    // BR-PRC-004 LoanCalculator.cs:45-50 — relationship discount bands, >= inclusive.
    public static decimal GetRelationshipDiscount(decimal depositBalance)
    {
        if (depositBalance >= 250000m) return 0.25m;
        if (depositBalance >= 100000m) return 0.10m;
        return 0.00m;
    }

    // BR-PRC-005 LoanCalculator.cs:52-61 — sum, then floor (strict <), then
    // ceiling (strict >), then round 2 dp AwayFromZero. Order is observable.
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

    // BR-PRC-006 LoanCalculator.cs:63-85 — fee = round(amount * rate) then
    // minimum then (TERM only) cap. BR-PRC-007 LoanCalculator.cs:82 — unknown product.
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
                throw new ArgumentException("Unknown product type: " + productType);
        }
        return fee;
    }

    // BR-AMT-001 LoanCalculator.cs:87-99 — level payment; zero-rate branch is an
    // exact == 0 test; BR-AMT-003 LoanCalculator.cs:89 — non-positive term rejection.
    public static decimal MonthlyPayment(decimal principal, decimal annualRatePct, int termMonths)
    {
        if (termMonths <= 0) throw new ArgumentException("termMonths must be positive");
        if (annualRatePct == 0m)
            return Math.Round(principal / termMonths, 2, MidpointRounding.AwayFromZero);

        decimal i = annualRatePct / 100m / 12m;
        // BR-AMT-001 LoanCalculator.cs:94-96 — (1+i)^n via IEEE double is what the
        // original VB6 code did (LEND-4471); kept for penny-for-penny compatibility
        // with historical statements. This is the only floating-point step.
        decimal pow = (decimal)Math.Pow((double)(1m + i), termMonths);
        decimal payment = principal * i * pow / (pow - 1m);
        return Math.Round(payment, 2, MidpointRounding.AwayFromZero);
    }

    // BR-AMT-002 LoanCalculator.cs:101-134 — per-period interest rounding, final
    // payment absorbs drift (period == termMonths || principalPortion >= balance,
    // inclusive), and balance <= 0 break can end the schedule early.
    public static List<AmortizationRow> BuildSchedule(decimal principal, decimal annualRatePct, int termMonths)
    {
        var rows = new List<AmortizationRow>();
        decimal payment = MonthlyPayment(principal, annualRatePct, termMonths);
        decimal i = annualRatePct / 100m / 12m;
        decimal balance = principal;

        for (int period = 1; period <= termMonths; period++)
        {
            decimal interest = Math.Round(balance * i, 2, MidpointRounding.AwayFromZero);
            decimal principalPortion = payment - interest;
            decimal actualPayment = payment;

            if (period == termMonths || principalPortion >= balance)
            {
                // Final payment absorbs rounding drift.
                principalPortion = balance;
                actualPayment = balance + interest;
            }

            balance -= principalPortion;
            rows.Add(new AmortizationRow
            {
                Period = period,
                Payment = Math.Round(actualPayment, 2, MidpointRounding.AwayFromZero),
                Interest = interest,
                Principal = Math.Round(principalPortion, 2, MidpointRounding.AwayFromZero),
                Balance = Math.Round(balance, 2, MidpointRounding.AwayFromZero)
            });

            if (balance <= 0m) break;
        }
        return rows;
    }
}

// BR-AMT-002 LoanCalculator.cs:137-144 — row shape; property order is the legacy
// grid/CSV column order (BR-UI-002).
public class AmortizationRow
{
    public int Period { get; set; }
    public decimal Payment { get; set; }
    public decimal Interest { get; set; }
    public decimal Principal { get; set; }
    public decimal Balance { get; set; }
}
