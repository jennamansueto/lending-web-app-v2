using Contoso.Lending.Domain;
using Npgsql;

namespace Contoso.Lending.Api;

/// <summary>
/// Data access over the layer-1 Postgres schema, which keeps the legacy table and column
/// names in lower case (`borrower`, `loan_application`, `loan`, `payment_schedule`, `payment`).
/// The database holds no procedural logic: every rule runs in the domain library.
/// </summary>
public sealed class LendingRepository(string connectionString)
{
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    // BR-UI-005: case-insensitive substring match on legal name or tax id, ordered by legal
    // name; `active_loans` counts loan rows of any status.
    public async Task<IReadOnlyList<BorrowerRowDto>> SearchBorrowersAsync(string search, CancellationToken ct)
    {
        const string sql = """
            SELECT b.borrower_id, b.legal_name, b.tax_id, b.credit_score, b.deposit_balance,
                   b.years_in_business,
                   (SELECT COUNT(*) FROM loan l WHERE l.borrower_id = b.borrower_id) AS active_loans
              FROM borrower b
             WHERE UPPER(b.legal_name) LIKE @term OR b.tax_id LIKE @term
             ORDER BY b.legal_name
            """;
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("term", "%" + search.Trim().ToUpperInvariant() + "%");
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<BorrowerRowDto>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new BorrowerRowDto(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                reader.GetDecimal(4), reader.GetInt32(5), (int)reader.GetInt64(6)));
        }
        return rows;
    }

    public async Task<int?> GetBorrowerCreditScoreAsync(int borrowerId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT credit_score FROM borrower WHERE borrower_id = @id", connection);
        command.Parameters.AddWithValue("id", borrowerId);
        var result = await command.ExecuteScalarAsync(ct);
        return result is null ? null : Convert.ToInt32(result);
    }

    public async Task<IReadOnlyList<LoanScheduleRowDto>> GetScheduleAsync(int loanId, CancellationToken ct)
    {
        const string sql = """
            SELECT period_no, due_date, payment_amt, interest_amt, principal_amt, balance_after
              FROM payment_schedule WHERE loan_id = @id ORDER BY period_no
            """;
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", loanId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<LoanScheduleRowDto>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new LoanScheduleRowDto(
                reader.GetInt32(0),
                ReadDate(reader, 1).ToString("yyyy-MM-dd"),
                reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4), reader.GetDecimal(5)));
        }
        return rows;
    }

    /// <summary>Loan state the payoff rule reads: loan terms, due schedule rows and all late fees.</summary>
    public async Task<PayoffLoanState?> GetPayoffStateAsync(int loanId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);

        decimal principal;
        decimal annualRate;
        DateOnly fundedDate;
        await using (var loanCommand = new NpgsqlCommand(
            "SELECT principal, annual_rate, funded_date FROM loan WHERE loan_id = @id", connection))
        {
            loanCommand.Parameters.AddWithValue("id", loanId);
            await using var loanReader = await loanCommand.ExecuteReaderAsync(ct);
            if (!await loanReader.ReadAsync(ct)) return null;
            principal = loanReader.GetDecimal(0);
            annualRate = loanReader.GetDecimal(1);
            fundedDate = ReadDate(loanReader, 2);
        }

        var schedule = new List<PayoffScheduleRow>();
        await using (var scheduleCommand = new NpgsqlCommand(
            "SELECT period_no, due_date, balance_after FROM payment_schedule WHERE loan_id = @id ORDER BY period_no", connection))
        {
            scheduleCommand.Parameters.AddWithValue("id", loanId);
            await using var scheduleReader = await scheduleCommand.ExecuteReaderAsync(ct);
            while (await scheduleReader.ReadAsync(ct))
            {
                schedule.Add(new PayoffScheduleRow(
                    scheduleReader.GetInt32(0), ReadDate(scheduleReader, 1), scheduleReader.GetDecimal(2)));
            }
        }

        decimal lateFees;
        await using (var feeCommand = new NpgsqlCommand(
            "SELECT COALESCE(SUM(late_fee), 0) FROM payment WHERE loan_id = @id", connection))
        {
            feeCommand.Parameters.AddWithValue("id", loanId);
            lateFees = Convert.ToDecimal(await feeCommand.ExecuteScalarAsync(ct));
        }

        return new PayoffLoanState(loanId, principal, annualRate, fundedDate, schedule, lateFees);
    }

    // Legacy `LoanApplicationForm.SaveApplication`: DTI/LTV persisted rounded to 4 dp,
    // status SUBMITTED.
    public async Task<int> InsertApplicationAsync(CreateApplicationRequestDto request, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO loan_application
                (app_id, borrower_id, product_type, amount, term_months, credit_score, dti, ltv, status, created_at)
            VALUES
                (nextval('seq_loan_application'), @bid, @prod, @amt, @term, @score, @dti, @ltv, 'SUBMITTED', CURRENT_DATE)
            RETURNING app_id
            """;
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("bid", request.BorrowerId);
        command.Parameters.AddWithValue("prod", request.ProductType);
        command.Parameters.AddWithValue("amt", request.Amount);
        command.Parameters.AddWithValue("term", request.TermMonths);
        command.Parameters.AddWithValue("score", request.CreditScore);
        command.Parameters.AddWithValue("dti", Math.Round(request.Dti, 4));
        command.Parameters.AddWithValue("ltv", Math.Round(request.Ltv, 4));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
    }

    /// <summary>Books a loan and writes the schedule the service computed (BR-AMT-002 rows).</summary>
    public async Task<int> InsertLoanAsync(BookLoanRequestDto request, IReadOnlyList<AmortizationRow> schedule, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        int loanId;
        const string loanSql = """
            INSERT INTO loan
                (loan_id, app_id, borrower_id, product_type, principal, annual_rate, term_months, orig_fee, funded_date, status)
            VALUES
                (nextval('seq_loan'), @app, @bid, @prod, @principal, @rate, @term, @fee, @funded, 'ACTIVE')
            RETURNING loan_id
            """;
        await using (var command = new NpgsqlCommand(loanSql, connection, transaction))
        {
            command.Parameters.AddWithValue("app", request.AppId);
            command.Parameters.AddWithValue("bid", request.BorrowerId);
            command.Parameters.AddWithValue("prod", request.ProductType);
            command.Parameters.AddWithValue("principal", request.Principal);
            command.Parameters.AddWithValue("rate", request.AnnualRate);
            command.Parameters.AddWithValue("term", request.TermMonths);
            command.Parameters.AddWithValue("fee", request.OrigFee);
            command.Parameters.AddWithValue("funded", request.FundedDate);
            loanId = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        }

        const string rowSql = """
            INSERT INTO payment_schedule
                (loan_id, period_no, due_date, payment_amt, interest_amt, principal_amt, balance_after)
            VALUES (@loan, @period, @due, @payment, @interest, @principal, @balance)
            """;
        foreach (var row in schedule)
        {
            await using var command = new NpgsqlCommand(rowSql, connection, transaction);
            command.Parameters.AddWithValue("loan", loanId);
            command.Parameters.AddWithValue("period", row.Period);
            command.Parameters.AddWithValue("due", AddMonths(request.FundedDate, row.Period));
            command.Parameters.AddWithValue("payment", row.Payment);
            command.Parameters.AddWithValue("interest", row.Interest);
            command.Parameters.AddWithValue("principal", row.Principal);
            command.Parameters.AddWithValue("balance", row.Balance);
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return loanId;
    }

    /// <summary>
    /// Reads a legacy date column as a <see cref="DateOnly"/>. The layer-1 schema stores
    /// `funded_date`, `due_date` and `paid_date` as `timestamp(0)` (Oracle `DATE` carries a time
    /// component), which Npgsql surfaces as <see cref="DateTime"/>; the legacy data always has a
    /// midnight time-of-day, so only the date part is meaningful to the rules.
    /// </summary>
    private static DateOnly ReadDate(NpgsqlDataReader reader, int ordinal) =>
        DateOnly.FromDateTime(reader.GetDateTime(ordinal));

    /// <summary>Oracle `ADD_MONTHS` semantics: clamp to month length, keep end-of-month anchoring.</summary>
    internal static DateOnly AddMonths(DateOnly date, int months)
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
