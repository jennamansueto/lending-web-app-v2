using System;
using System.Collections.Generic;
using System.Linq;

namespace Contoso.Lending.Domain;

public sealed record PayoffScheduleRow(int PeriodNo, DateOnly DueDate, decimal BalanceAfter);

/// <summary>State of one loan as the payoff quote reads it (legacy LOAN / PAYMENT_SCHEDULE / PAYMENT).</summary>
public sealed record PayoffLoanState(
    int LoanId,
    decimal Principal,
    decimal AnnualRate,
    DateOnly FundedDate,
    IReadOnlyList<PayoffScheduleRow> Schedule,
    decimal LateFeeTotal);

public sealed record PayoffQuote(
    int LoanId,
    DateOnly AsOf,
    decimal Balance,
    decimal AccruedInterest,
    decimal UnpaidLateFees,
    decimal Payoff);

/// <summary>
/// Servicing rules extracted 1:1 from legacy `database/plsql/pkg_lending.sql`
/// (`PKG_LENDING.CALC_LATE_FEE`, `PKG_LENDING.GET_PAYOFF_AMOUNT`). Oracle `ROUND(x, 2)` is
/// half-away-from-zero, reproduced with <see cref="MidpointRounding.AwayFromZero"/> over
/// decimal; interest accrual is actual/365 simple interest.
/// </summary>
public static class ServicingEngine
{
    private static decimal OracleRound(decimal value, int digits = 2) =>
        Math.Round(value, digits, MidpointRounding.AwayFromZero);

    // BR-SVC-001 (composite of BR-SVC-003/004/005): 10-day inclusive grace, then 5% of the
    // payment rounded to 2dp, floored at $25 and capped at $150.
    public static decimal CalcLateFee(decimal paymentAmount, int daysLate)
    {
        // BR-SVC-003: grace test is inclusive — day 10 is free, day 11 is charged.
        if (daysLate <= 10) return 0m;

        // BR-SVC-004
        decimal fee = OracleRound(paymentAmount * 0.05m);

        // BR-SVC-005. Legacy quirk: the $25 floor applies to any late payment, including a
        // $0.00 payment amount, which therefore still owes $25.
        if (fee < 25m) fee = 25m;
        else if (fee > 150m) fee = 150m;

        return fee;
    }

    // BR-SVC-002 (composite of BR-SVC-006..010): payoff = schedule balance + actual/365
    // accrual (floored at 0) + all recorded late fees, rounded to 2dp.
    public static PayoffQuote CalcPayoff(PayoffLoanState loan, DateOnly asOf)
    {
        // BR-SVC-006: MIN(BALANCE_AFTER) over the rows due on or before the as-of date —
        // legacy quirk: the minimum, not the balance of the latest due row. Falls back to
        // LOAN.PRINCIPAL / LOAN.FUNDED_DATE when nothing is due yet.
        var due = loan.Schedule.Where(row => row.DueDate <= asOf).ToList();
        decimal balance;
        DateOnly lastDue;
        if (due.Count > 0)
        {
            balance = due.Min(row => row.BalanceAfter);
            lastDue = due.Max(row => row.DueDate);
        }
        else
        {
            balance = loan.Principal;
            lastDue = loan.FundedDate;
        }

        // BR-SVC-007: actual/365 simple interest since the last due date.
        int days = asOf.DayNumber - lastDue.DayNumber;
        decimal accrued = OracleRound(balance * (loan.AnnualRate / 100m) * days / 365m);
        // BR-SVC-008: negative accrual (as-of before the last due date) is floored at 0.
        if (accrued < 0m) accrued = 0m;

        // BR-SVC-009: adds SUM(PAYMENT.LATE_FEE) over ALL payment rows for the loan — legacy
        // quirk: there is no paid/unpaid flag, so fees are quoted even after full amortization.
        decimal lateFees = loan.LateFeeTotal;

        // BR-SVC-010
        decimal payoff = OracleRound(balance + accrued + lateFees);
        return new PayoffQuote(loan.LoanId, asOf, balance, accrued, lateFees, payoff);
    }
}
