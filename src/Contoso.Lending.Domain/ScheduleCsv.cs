using System.Text;

namespace Contoso.Lending.Domain;

/// <summary>
/// BR-UI-010 StatementsForm.cs:53-68 (btnExport_Click) — CSV export of the
/// payment schedule: legacy uppercase header, comma-separated raw cell values,
/// CRLF after every row including the last.
/// </summary>
public static class ScheduleCsv
{
    public const string Header = "PERIOD_NO,DUE_DATE,PAYMENT_AMT,INTEREST_AMT,PRINCIPAL_AMT,BALANCE_AFTER";

    public static string Build(IEnumerable<(int PeriodNo, DateTime DueDate, decimal PaymentAmt, decimal InterestAmt, decimal PrincipalAmt, decimal BalanceAfter)> rows)
    {
        var sb = new StringBuilder(Header + "\r\n");
        foreach (var row in rows)
        {
            sb.Append(row.PeriodNo);
            sb.Append(',');
            // Legacy appended the raw grid cell values under the desktop's en-US culture.
            sb.Append(row.DueDate.ToString(LegacyCulture.EnUs));
            sb.Append(',');
            sb.Append(row.PaymentAmt.ToString(LegacyCulture.EnUs));
            sb.Append(',');
            sb.Append(row.InterestAmt.ToString(LegacyCulture.EnUs));
            sb.Append(',');
            sb.Append(row.PrincipalAmt.ToString(LegacyCulture.EnUs));
            sb.Append(',');
            sb.Append(row.BalanceAfter.ToString(LegacyCulture.EnUs));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }
}
