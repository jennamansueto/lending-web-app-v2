using Contoso.Lending.Domain;
using Npgsql;

namespace Contoso.Lending.Data;

public sealed record LoanSnapshot(
    int LoanId,
    decimal Principal,
    decimal AnnualRate,
    int TermMonths,
    DateTime FundedDate);

public sealed record PaymentScheduleRow(
    int PeriodNo,
    DateTime DueDate,
    decimal PaymentAmt,
    decimal InterestAmt,
    decimal PrincipalAmt,
    decimal BalanceAfter);

/// <summary>
/// Data access only — no business rules. Payoff aggregation (MIN/MAX, fallback,
/// accrual, late-fee sum semantics) lives in Domain.ServicingCalculator
/// (BR-SVC-002); this class returns raw rows.
/// </summary>
public sealed class LoanRepository(NpgsqlDataSource dataSource)
{
    public async Task<LoanSnapshot?> GetLoanAsync(int loanId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT loan_id, principal, annual_rate, term_months, funded_date FROM loan WHERE loan_id = @id");
        cmd.Parameters.AddWithValue("id", (decimal)loanId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new LoanSnapshot(
            (int)reader.GetDecimal(0),
            reader.GetDecimal(1),
            reader.GetDecimal(2),
            (int)reader.GetDecimal(3),
            reader.GetDateTime(4));
    }

    // Legacy SQL (StatementsForm.cs:47-51): SELECT PERIOD_NO, DUE_DATE, PAYMENT_AMT,
    // INTEREST_AMT, PRINCIPAL_AMT, BALANCE_AFTER FROM PAYMENT_SCHEDULE
    // WHERE LOAN_ID = :id ORDER BY PERIOD_NO
    public async Task<List<PaymentScheduleRow>> GetScheduleAsync(int loanId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("""
            SELECT period_no, due_date, payment_amt, interest_amt, principal_amt, balance_after
              FROM payment_schedule
             WHERE loan_id = @id
             ORDER BY period_no
            """);
        cmd.Parameters.AddWithValue("id", (decimal)loanId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<PaymentScheduleRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PaymentScheduleRow(
                (int)reader.GetDecimal(0),
                reader.GetDateTime(1),
                reader.GetDecimal(2),
                reader.GetDecimal(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5)));
        }
        return rows;
    }

    public async Task<List<ScheduleEntry>> GetScheduleEntriesAsync(int loanId, CancellationToken ct = default)
    {
        var rows = await GetScheduleAsync(loanId, ct);
        return rows.Select(r => new ScheduleEntry(r.DueDate, r.BalanceAfter)).ToList();
    }

    // Legacy (pkg_lending.sql:72-75): SELECT NVL(SUM(P.LATE_FEE), 0) FROM PAYMENT P
    // WHERE P.LOAN_ID = :id — all recorded fees, paid or not.
    public async Task<decimal> GetLateFeeSumAsync(int loanId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT COALESCE(SUM(p.late_fee), 0) FROM payment p WHERE p.loan_id = @id");
        cmd.Parameters.AddWithValue("id", (decimal)loanId);
        object? result = await cmd.ExecuteScalarAsync(ct);
        return (decimal)result!;
    }

    public async Task<int> BookLoanAsync(int? appId, int borrowerId, string productType, decimal principal,
        decimal annualRate, int termMonths, decimal origFee, DateTime fundedDate,
        IReadOnlyList<AmortizationRow> schedule, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using var insertLoan = new NpgsqlCommand("""
            INSERT INTO loan
                (loan_id, app_id, borrower_id, product_type, principal, annual_rate, term_months, orig_fee, funded_date)
            VALUES
                (nextval('seq_loan'), @app, @bid, @prod, @principal, @rate, @term, @fee, @funded)
            RETURNING loan_id
            """, conn, tx);
        insertLoan.Parameters.AddWithValue("app", appId is null ? DBNull.Value : (decimal)appId.Value);
        insertLoan.Parameters.AddWithValue("bid", (decimal)borrowerId);
        insertLoan.Parameters.AddWithValue("prod", productType);
        insertLoan.Parameters.AddWithValue("principal", principal);
        insertLoan.Parameters.AddWithValue("rate", annualRate);
        insertLoan.Parameters.AddWithValue("term", (decimal)termMonths);
        insertLoan.Parameters.AddWithValue("fee", origFee);
        insertLoan.Parameters.AddWithValue("funded", fundedDate);
        int loanId = (int)(decimal)(await insertLoan.ExecuteScalarAsync(ct))!;

        foreach (var row in schedule)
        {
            await using var insertRow = new NpgsqlCommand("""
                INSERT INTO payment_schedule
                    (loan_id, period_no, due_date, payment_amt, interest_amt, principal_amt, balance_after)
                VALUES
                    (@loan, @period, @due, @payment, @interest, @principal, @balance)
                """, conn, tx);
            insertRow.Parameters.AddWithValue("loan", (decimal)loanId);
            insertRow.Parameters.AddWithValue("period", (decimal)row.Period);
            // Due dates follow the legacy schedule generator
            // (generate_schedules.sql:19-43): funded date + N months.
            insertRow.Parameters.AddWithValue("due", fundedDate.AddMonths(row.Period));
            insertRow.Parameters.AddWithValue("payment", row.Payment);
            insertRow.Parameters.AddWithValue("interest", row.Interest);
            insertRow.Parameters.AddWithValue("principal", row.Principal);
            insertRow.Parameters.AddWithValue("balance", row.Balance);
            await insertRow.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return loanId;
    }
}
