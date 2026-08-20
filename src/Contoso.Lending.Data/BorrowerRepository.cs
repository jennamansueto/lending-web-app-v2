using Npgsql;

namespace Contoso.Lending.Data;

public sealed record BorrowerRow(
    int BorrowerId,
    string LegalName,
    string TaxId,
    int CreditScore,
    decimal DepositBalance,
    int YearsInBusiness,
    int ActiveLoans);

/// <summary>
/// Data access only — no business rules. The LIKE semantics (wildcards, bound
/// term shape, case sensitivity) are the Domain's BR-PQL-002; this class just
/// executes the same SQL shape the legacy form ran (BorrowerLookupForm.cs:41-51),
/// translated to the Postgres schema in database/postgres/schema.sql.
/// </summary>
public sealed class BorrowerRepository(NpgsqlDataSource dataSource)
{
    // Legacy SQL: WHERE UPPER(B.LEGAL_NAME) LIKE :term OR B.TAX_ID LIKE :term
    //             ORDER BY B.LEGAL_NAME
    // ACTIVE_LOANS deliberately counts every loan row regardless of status.
    private const string SearchSql = """
        SELECT b.borrower_id,
               b.legal_name,
               b.tax_id,
               b.credit_score,
               b.deposit_balance,
               b.years_in_business,
               (SELECT COUNT(*) FROM loan l WHERE l.borrower_id = b.borrower_id) AS active_loans
          FROM borrower b
         WHERE UPPER(b.legal_name) LIKE @term OR b.tax_id LIKE @term
         ORDER BY b.legal_name
        """;

    public async Task<List<BorrowerRow>> SearchAsync(string boundTerm, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(SearchSql);
        cmd.Parameters.AddWithValue("term", boundTerm);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<BorrowerRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new BorrowerRow(
                (int)reader.GetDecimal(0),
                reader.GetString(1),
                reader.GetString(2),
                (int)reader.GetDecimal(3),
                reader.GetDecimal(4),
                (int)reader.GetDecimal(5),
                reader.GetInt32(6)));
        }
        return rows;
    }

    public async Task<int?> GetCreditScoreAsync(int borrowerId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT credit_score FROM borrower WHERE borrower_id = @id");
        cmd.Parameters.AddWithValue("id", (decimal)borrowerId);
        object? result = await cmd.ExecuteScalarAsync(ct);
        return result is null ? null : (int)(decimal)result;
    }
}
