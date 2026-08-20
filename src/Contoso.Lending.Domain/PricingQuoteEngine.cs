namespace Contoso.Lending.Domain;

public sealed class PricingQuote
{
    public decimal Rate { get; init; }
    public decimal OriginationFee { get; init; }
    public decimal MonthlyPayment { get; init; }
    public required string RateLabel { get; init; }
    public required string FeeLabel { get; init; }
    public required string PaymentLabel { get; init; }
    public required IReadOnlyList<AmortizationRow> Schedule { get; init; }
}

/// <summary>
/// BR-UI-001/BR-UI-002 PricingForm.cs:76-101 (btnPrice_Click) — the pricing screen's
/// composite quote: priced rate, origination fee, monthly payment at the priced
/// rate, and the full amortization schedule, with byte-exact label strings.
/// </summary>
public static class PricingQuoteEngine
{
    public static PricingQuote Quote(string productType, decimal amount, int termMonths,
        int creditScore, decimal ltv, decimal depositBalance)
    {
        // BR-UI-014 PricingForm.cs:85 — blank deposits box is treated as 0m
        // (handled by callers that accept an optional value; pass 0m when absent).
        decimal rate = LoanCalculator.PriceRate(productType, creditScore, ltv, depositBalance);
        decimal fee = LoanCalculator.CalcOriginationFee(amount, productType);
        decimal payment = LoanCalculator.MonthlyPayment(amount, rate, termMonths);

        return new PricingQuote
        {
            Rate = rate,
            OriginationFee = fee,
            MonthlyPayment = payment,
            // BR-UI-001 PricingForm.cs:91-93 — exact labels: "0.00" rate with a
            // space before '%', "C2" currency for fee and payment.
            RateLabel = "Rate: " + LegacyCulture.Fixed2(rate) + " %",
            FeeLabel = "Origination fee: " + LegacyCulture.Currency(fee),
            PaymentLabel = "Monthly payment: " + LegacyCulture.Currency(payment),
            // BR-UI-002 PricingForm.cs:95 — grid binds the full schedule.
            Schedule = LoanCalculator.BuildSchedule(amount, rate, termMonths),
        };
    }

    // BR-UI-004/BR-UI-005 PricingForm.cs:104-118 (btnLateFee_Click). LEND-5102: the screen
    // reuses the LOAN AMOUNT box as the late-fee payment amount (PricingForm.cs:108,
    // "yes, reuses amount box; known quirk LEND-5102"). Callers of the pricing
    // late-fee action must pass the loan amount, not a payment amount. Do not fix.
    public static (decimal Fee, string Label) LateFeeFromPricingScreen(decimal loanAmountAsPayment, int daysLate)
    {
        decimal fee = ServicingCalculator.CalcLateFee(loanAmountAsPayment, daysLate);
        return (fee, ServicingCalculator.LateFeeLabel(fee));
    }
}
