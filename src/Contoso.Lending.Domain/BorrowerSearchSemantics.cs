using System.Text;
using System.Text.RegularExpressions;

namespace Contoso.Lending.Domain;

/// <summary>
/// BR-PQL-002 BorrowerLookupForm.cs:41-51 (RunSearch) — the exact matching
/// semantics of the legacy borrower search:
///   WHERE UPPER(B.LEGAL_NAME) LIKE :term OR B.TAX_ID LIKE :term
///   ORDER BY B.LEGAL_NAME
/// Quirks preserved verbatim (do not fix):
///  - the bound term is "%" + Trim().ToUpperInvariant() + "%", so an empty or
///    whitespace search binds "%%" and returns every borrower;
///  - '%' and '_' typed by the user remain live SQL LIKE wildcards;
///  - the name side is uppercased (case-insensitive) while the TAX_ID side is
///    compared case-sensitively against the uppercased term;
///  - ACTIVE_LOANS counts every LOAN row for the borrower regardless of STATUS.
/// </summary>
public static class BorrowerSearchSemantics
{
    // BR-PQL-002 BorrowerLookupForm.cs:43 — bound term construction.
    public static string BuildBoundTerm(string searchText)
    {
        return "%" + searchText.Trim().ToUpperInvariant() + "%";
    }

    // BR-PQL-002 BorrowerLookupForm.cs:46 — row matches when UPPER(LEGAL_NAME)
    // LIKE term OR TAX_ID LIKE term (tax id case-sensitive).
    public static bool Matches(string boundTerm, string legalName, string taxId)
    {
        return Like(legalName.ToUpperInvariant(), boundTerm) || Like(taxId, boundTerm);
    }

    // SQL LIKE with '%' (any run) and '_' (any single char); everything else literal.
    private static bool Like(string value, string pattern)
    {
        var sb = new StringBuilder("^");
        foreach (char c in pattern)
        {
            if (c == '%') sb.Append(".*");
            else if (c == '_') sb.Append('.');
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return Regex.IsMatch(value, sb.ToString(), RegexOptions.Singleline);
    }
}
