using System.Text;

namespace Contoso.Lending.Migrator;

/// <summary>
/// Builds, for each engine, the SQL that renders the canonical row encoding
/// and the per-column sums defined by the legacy checksum contract
/// (lending-desktop-app <c>database/checks/README.md</c>, algorithm_version 1).
///
/// Both sides render their own values server-side; only the SHA-256 fold over
/// the rendered rows is done in this process, so a formatting difference
/// between the engines shows up as a mismatch instead of being hidden.
/// </summary>
internal static class CanonicalSql
{
    public const string NullSentinel = @"\N";
    public const string DateFormat = "YYYY-MM-DD HH24:MI:SS";

    /// <summary>Oracle <c>TO_CHAR</c> mask: no padding, no grouping, fixed scale.</summary>
    private static string OracleMask(int scale, int integerDigits) =>
        "FM" + new string('9', integerDigits) + "0" + (scale > 0 ? "." + new string('0', scale) : string.Empty);

    private static string OracleValue(Column c, string expr, int integerDigits) => c.Kind switch
    {
        ColumnKind.Integer or ColumnKind.Decimal => $"TO_CHAR({expr}, '{OracleMask(c.Scale, integerDigits)}')",
        ColumnKind.Date => $"TO_CHAR({expr}, '{DateFormat}')",
        _ => expr,
    };

    private static string OracleNullable(Column c, string rendered) =>
        c.Nullable || c.Kind == ColumnKind.Text ? $"NVL({rendered}, '{NullSentinel}')" : rendered;

    public static string OracleRowQuery(Table t)
    {
        var parts = t.Columns.Select(c =>
            OracleNullable(c, OracleValue(c, c.Name.ToUpperInvariant(), integerDigits: 10)));
        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(string.Join(" || CHR(31) || ", parts));
        sb.Append(" FROM ").Append(t.Name.ToUpperInvariant());
        sb.Append(" ORDER BY ").Append(string.Join(", ", t.PrimaryKey.Select(k => k.ToUpperInvariant())));
        return sb.ToString();
    }

    public static string OracleCountQuery(Table t) =>
        $"SELECT COUNT(*) FROM {t.Name.ToUpperInvariant()}";

    /// <summary>One row per numeric column: <c>column_name, sum_as_text</c>.</summary>
    public static string OracleSumQuery(Table t)
    {
        var selects = t.NumericColumns.Select(c =>
        {
            var upper = c.Name.ToUpperInvariant();
            // Sums need room for the total, not just one value's declared precision.
            var rendered = $"TO_CHAR(SUM({upper}), '{OracleMask(c.Scale, integerDigits: 30)}')";
            return $"SELECT '{c.Name}' AS col, NVL({rendered}, '{NullSentinel}') AS val FROM {t.Name.ToUpperInvariant()}";
        });
        return string.Join(" UNION ALL ", selects);
    }

    private static string PostgresValue(Column c) => c.Kind switch
    {
        ColumnKind.Integer or ColumnKind.Decimal => $"{c.Name}::text",
        ColumnKind.Date => $"to_char({c.Name}, '{DateFormat}')",
        _ => c.Name,
    };

    private static string PostgresNullable(Column c, string rendered) =>
        c.Nullable || c.Kind == ColumnKind.Text ? $"coalesce({rendered}, '{NullSentinel}')" : rendered;

    public static string PostgresRowQuery(Table t)
    {
        var parts = t.Columns.Select(c => PostgresNullable(c, PostgresValue(c)));
        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(string.Join(" || chr(31) || ", parts));
        sb.Append(" FROM ").Append(t.Name);
        sb.Append(" ORDER BY ").Append(string.Join(", ", t.PrimaryKey));
        return sb.ToString();
    }

    public static string PostgresCountQuery(Table t) => $"SELECT count(*) FROM {t.Name}";

    public static string PostgresSumQuery(Table t)
    {
        var selects = t.NumericColumns.Select(c =>
            $"SELECT '{c.Name}' AS col, coalesce(sum({c.Name})::text, '{NullSentinel}') AS val FROM {t.Name}");
        return string.Join(" UNION ALL ", selects);
    }
}
