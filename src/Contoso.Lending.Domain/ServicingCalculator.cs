namespace Contoso.Lending.Domain;

// BR-SVC-002 pkg_lending.sql:52-58 — the payoff query reads DUE_DATE and
// BALANCE_AFTER from PAYMENT_SCHEDULE; aggregation happens in the rule below.
public sealed record ScheduleEntry(DateTime DueDate, decimal BalanceAfter);

/// <summary>
/// Ports of the Oracle stored functions in database/plsql/pkg_lending.sql into
/// the service layer. The Postgres side holds no logic; these are the only
/// implementations of the servicing rules.
/// </summary>
public static class ServicingCalculator
{
    // BR-SVC-001 pkg_lending.sql:20-39 (CALC_LATE_FEE) — <= 10 days is free
    // (negative days included); 5% of the payment rounded half-away-from-zero
    // (Oracle ROUND); floor 25, cap 150, floor checked first (ELSIF).
    public static decimal CalcLateFee(decimal paymentAmount, int daysLate)
    {
        if (daysLate <= 10)
        {
            return 0m;
        }

        decimal fee = Math.Round(paymentAmount * 0.05m, 2, MidpointRounding.AwayFromZero);

        if (fee < 25m)
        {
            fee = 25m;
        }
        else if (fee > 150m)
        {
            fee = 150m;
        }

        return fee;
    }

    // BR-UI-004 PricingForm.cs:111 — the desktop shows the fee as "Late fee: " + C2.
    // Kept with the rule so the string lives in one place.
    public static string LateFeeLabel(decimal fee)
    {
        return "Late fee: " + LegacyCulture.Currency(fee);
    }

    // BR-SVC-002 pkg_lending.sql:43-77 (GET_PAYOFF_AMOUNT). Quirks preserved
    // verbatim — do not fix any of them:
    //  - MIN(BALANCE_AFTER) and MAX(DUE_DATE) are taken independently over the
    //    schedule rows with DUE_DATE <= asOf (pkg_lending.sql:52-58);
    //  - when no row qualifies, falls back to LOAN.PRINCIPAL / FUNDED_DATE
    //    (pkg_lending.sql:60-64);
    //  - interest accrues simple actual/365 from that date, ROUND(...,2)
    //    half-away-from-zero, floored at zero (pkg_lending.sql:66-70);
    //  - adds SUM(PAYMENT.LATE_FEE) over ALL recorded payments — despite the
    //    "unpaid" wording in the spec, paid fees are never excluded
    //    (pkg_lending.sql:72-75);
    //  - payoff is based on scheduled due dates, not payment history, so a loan
    //    past its final due date quotes 0 (plus late fees) even if unpaid.
    public static decimal GetPayoffAmount(decimal annualRate, decimal principal, DateTime fundedDate,
        IReadOnlyList<ScheduleEntry> schedule, decimal lateFeeSum, DateTime asOf)
    {
        return GetPayoffQuote(annualRate, principal, fundedDate, schedule, lateFeeSum, asOf).Payoff;
    }

    // Same rule, exposing the intermediate values the payoff endpoint reports.
    public static PayoffQuote GetPayoffQuote(decimal annualRate, decimal principal, DateTime fundedDate,
        IReadOnlyList<ScheduleEntry> schedule, decimal lateFeeSum, DateTime asOf)
    {
        decimal? balance = null;
        DateTime? lastDue = null;
        foreach (var row in schedule)
        {
            if (row.DueDate <= asOf)
            {
                if (balance is null || row.BalanceAfter < balance.Value) balance = row.BalanceAfter;
                if (lastDue is null || row.DueDate > lastDue.Value) lastDue = row.DueDate;
            }
        }

        if (balance is null)
        {
            balance = principal;
            lastDue = fundedDate;
        }

        int days = (asOf.Date - lastDue!.Value.Date).Days;
        decimal accrued = Math.Round(balance.Value * (annualRate / 100m) * days / 365m, 2, MidpointRounding.AwayFromZero);
        if (accrued < 0m)
        {
            accrued = 0m;
        }

        return new PayoffQuote
        {
            Balance = balance.Value,
            AccruedInterest = accrued,
            LateFees = lateFeeSum,
            Payoff = Math.Round(balance.Value + accrued + lateFeeSum, 2, MidpointRounding.AwayFromZero),
        };
    }
}

// BR-SVC-002 pkg_lending.sql:76 — payoff = ROUND(balance + accrued + late fees, 2).
public sealed class PayoffQuote
{
    public decimal Balance { get; init; }
    public decimal AccruedInterest { get; init; }
    public decimal LateFees { get; init; }
    public decimal Payoff { get; init; }
}
