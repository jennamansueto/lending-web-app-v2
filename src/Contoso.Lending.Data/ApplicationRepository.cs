using Contoso.Lending.Domain;
using Npgsql;

namespace Contoso.Lending.Data;

/// <summary>
/// Data access only — no business rules. The persisted values (ToEven-rounded
/// DTI/LTV, status 'SUBMITTED') are computed by Domain.ApplicationPersistence
/// (BR-ELG-010); this class only executes the INSERT, mirroring the legacy
/// SaveApplication SQL (LoanApplicationForm.cs:167-181) against the Postgres
/// schema: SEQ_LOAN_APPLICATION.NEXTVAL -> nextval('seq_loan_application'),
/// SYSDATE -> localtimestamp(0).
/// </summary>
public sealed class ApplicationRepository(NpgsqlDataSource dataSource)
{
    private const string InsertSql = """
        INSERT INTO loan_application
            (app_id, borrower_id, product_type, amount, term_months, credit_score, dti, ltv, status, created_at)
        VALUES
            (nextval('seq_loan_application'), @bid, @prod, @amt, @term, @score, @dti, @ltv, @status, localtimestamp(0))
        RETURNING app_id
        """;

    public async Task<int> InsertAsync(PersistedApplication app, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(InsertSql);
        cmd.Parameters.AddWithValue("bid", (decimal)app.BorrowerId);
        cmd.Parameters.AddWithValue("prod", app.ProductType);
        cmd.Parameters.AddWithValue("amt", app.Amount);
        cmd.Parameters.AddWithValue("term", (decimal)app.TermMonths);
        cmd.Parameters.AddWithValue("score", (decimal)app.CreditScore);
        cmd.Parameters.AddWithValue("dti", app.Dti);
        cmd.Parameters.AddWithValue("ltv", app.Ltv);
        cmd.Parameters.AddWithValue("status", app.Status);
        object? result = await cmd.ExecuteScalarAsync(ct);
        return (int)(decimal)result!;
    }
}
