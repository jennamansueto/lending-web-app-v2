using System;
using System.Collections.Generic;
using System.Linq;
using Contoso.Lending.Domain;

namespace Contoso.Lending.ParityTests;

/// <summary>
/// The BR-SVC-002 golden records were produced against the untouched seeded Oracle database
/// (`database/seed/seed_data.sql` + `database/seed/generate_schedules.sql` in the legacy repo),
/// as recorded in each record's `dbState` field. This fixture reproduces that state so the
/// payoff rule can be exercised as a pure function.
///
/// The schedules are rebuilt exactly as `generate_schedules.sql` (BR-AMT-004) does it: Oracle
/// `POWER` over NUMBER — exact decimal exponentiation, not the client's double round trip —
/// `ROUND` half away from zero, final-period drift absorption, and `ADD_MONTHS(funded, p)`
/// due dates.
/// </summary>
public static class LegacySeededDatabase
{
    private sealed record SeedLoan(int LoanId, decimal Principal, decimal AnnualRate, int TermMonths, DateOnly FundedDate);

    private static readonly SeedLoan[] Loans =
    {
        new(1, 450000.00m, 7.25m, 84, new DateOnly(2021, 4, 30)),
        new(2, 175000.00m, 8.15m, 60, new DateOnly(2022, 2, 1)),
        new(3, 820000.00m, 6.35m, 120, new DateOnly(2020, 8, 20)),
        new(4, 250000.00m, 7.00m, 36, new DateOnly(2023, 3, 10)),
        new(5, 310000.00m, 8.30m, 72, new DateOnly(2021, 12, 1))
    };

    /// <summary>SUM(PAYMENT.LATE_FEE) per loan over the seeded PAYMENT rows.</summary>
    private static readonly Dictionary<int, decimal> LateFeeTotals = new()
    {
        [1] = 0.00m,
        [2] = 150.00m,
        [3] = 0.00m,
        [4] = 0.00m,
        [5] = 0.00m
    };

    private static readonly Dictionary<int, PayoffLoanState> States = Loans.ToDictionary(
        loan => loan.LoanId,
        loan => new PayoffLoanState(
            loan.LoanId,
            loan.Principal,
            loan.AnnualRate,
            loan.FundedDate,
            BuildOracleSchedule(loan),
            LateFeeTotals[loan.LoanId]));

    public static PayoffLoanState Loan(int loanId) => States[loanId];

    private static IReadOnlyList<PayoffScheduleRow> BuildOracleSchedule(SeedLoan loan)
    {
        decimal i = loan.AnnualRate / 100m / 12m;
        decimal payment;
        if (i == 0m)
        {
            payment = OracleRound(loan.Principal / loan.TermMonths);
        }
        else
        {
            decimal pow = Power(1m + i, loan.TermMonths);
            payment = OracleRound(loan.Principal * i * pow / (pow - 1m));
        }

        var rows = new List<PayoffScheduleRow>();
        decimal balance = loan.Principal;
        for (int period = 1; period <= loan.TermMonths; period++)
        {
            decimal interest = OracleRound(balance * i);
            decimal principalPart = payment - interest;
            if (period == loan.TermMonths || principalPart >= balance)
            {
                principalPart = balance;
            }
            balance -= principalPart;
            rows.Add(new PayoffScheduleRow(period, AddMonths(loan.FundedDate, period), OracleRound(balance)));
            if (balance <= 0m) break;
        }
        return rows;
    }

    private static decimal Power(decimal value, int exponent)
    {
        decimal result = 1m;
        for (int n = 0; n < exponent; n++) result *= value;
        return result;
    }

    private static decimal OracleRound(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Oracle `ADD_MONTHS`: clamps to month length and keeps end-of-month anchoring.</summary>
    private static DateOnly AddMonths(DateOnly date, int months)
    {
        int totalMonths = (date.Year * 12 + date.Month - 1) + months;
        int year = totalMonths / 12;
        int month = totalMonths % 12 + 1;
        int daysInTarget = DateTime.DaysInMonth(year, month);
        bool endOfMonth = date.Day == DateTime.DaysInMonth(date.Year, date.Month);
        int day = endOfMonth ? daysInTarget : Math.Min(date.Day, daysInTarget);
        return new DateOnly(year, month, day);
    }
}
