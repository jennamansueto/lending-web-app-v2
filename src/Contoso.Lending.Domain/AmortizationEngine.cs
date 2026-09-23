using System;
using System.Collections.Generic;

namespace Contoso.Lending.Domain;

/// <summary>
/// Amortization rules extracted 1:1 from legacy `src/LendingDesk/Core/LoanCalculator.cs`.
/// </summary>
public static class AmortizationEngine
{
    // BR-AMT-001: annuity monthly payment, rounded 2dp half away from zero; a 0% rate
    // divides the principal over the term.
    public static decimal MonthlyPayment(decimal principal, decimal annualRatePct, int termMonths)
    {
        // BR-AMT-003
        if (termMonths <= 0) throw new ArgumentException("termMonths must be positive");
        if (annualRatePct == 0m)
            return Math.Round(principal / termMonths, 2, MidpointRounding.AwayFromZero);

        decimal i = annualRatePct / 100m / 12m;
        // Legacy quirk (inherited from the VB6 original): (1+i)^n is computed in double and
        // round-tripped back to decimal, so payments carry that binary-float residue. Kept
        // verbatim for penny-for-penny compatibility with historical statements.
        decimal pow = (decimal)Math.Pow((double)(1m + i), termMonths);
        decimal payment = principal * i * pow / (pow - 1m);
        return Math.Round(payment, 2, MidpointRounding.AwayFromZero);
    }

    // BR-AMT-002: schedule with per-period interest rounding; the final period (or any
    // period whose principal portion would overrun the balance) absorbs rounding drift.
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

public sealed class AmortizationRow
{
    public int Period { get; set; }
    public decimal Payment { get; set; }
    public decimal Interest { get; set; }
    public decimal Principal { get; set; }
    public decimal Balance { get; set; }
}
