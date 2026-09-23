namespace Contoso.Lending.Migrator;

internal enum ColumnKind
{
    Integer,
    Decimal,
    Text,
    Date,
}

/// <param name="Name">Legacy column name, lower case (identical on both sides).</param>
/// <param name="Kind">Value family, which drives the canonical text encoding.</param>
/// <param name="Scale">Declared decimal scale; 0 for every non-decimal kind.</param>
/// <param name="PgType">Postgres column type, used by the binary COPY writer.</param>
internal sealed record Column(string Name, ColumnKind Kind, int Scale, bool Nullable, string PgType)
{
    public bool IsNumeric => Kind is ColumnKind.Integer or ColumnKind.Decimal;
}

internal sealed record Table(string Name, IReadOnlyList<Column> Columns, IReadOnlyList<string> PrimaryKey)
{
    public IEnumerable<Column> NumericColumns => Columns.Where(c => c.IsNumeric);
}

/// <summary>
/// The five legacy tables in foreign-key dependency order, with columns in
/// declared (Oracle <c>COLUMN_ID</c>) order — the order the checksum's
/// canonical row encoding depends on.
/// </summary>
internal static class Schema
{
    private static Column Int(string name, bool nullable = false, string pg = "integer") =>
        new(name, ColumnKind.Integer, 0, nullable, pg);

    private static Column Small(string name, bool nullable = false) =>
        new(name, ColumnKind.Integer, 0, nullable, "smallint");

    private static Column Dec(string name, int scale, bool nullable = false) =>
        new(name, ColumnKind.Decimal, scale, nullable, "numeric");

    private static Column Text(string name, bool nullable = false) =>
        new(name, ColumnKind.Text, 0, nullable, "varchar");

    private static Column Date(string name, bool nullable = false) =>
        new(name, ColumnKind.Date, 0, nullable, "timestamp");

    public static readonly IReadOnlyList<Table> Tables =
    [
        new Table("borrower",
        [
            Int("borrower_id"),
            Text("legal_name"),
            Text("tax_id"),
            Small("credit_score"),
            Dec("deposit_balance", 2),
            Small("years_in_business"),
            Date("created_at"),
        ], ["borrower_id"]),

        new Table("loan_application",
        [
            Int("app_id"),
            Int("borrower_id"),
            Text("product_type"),
            Dec("amount", 2),
            Small("term_months"),
            Small("credit_score"),
            Dec("dti", 4, nullable: true),
            Dec("ltv", 4, nullable: true),
            Text("status"),
            Date("created_at"),
        ], ["app_id"]),

        new Table("loan",
        [
            Int("loan_id"),
            Int("app_id", nullable: true),
            Int("borrower_id"),
            Text("product_type"),
            Dec("principal", 2),
            Dec("annual_rate", 3),
            Small("term_months"),
            Dec("orig_fee", 2),
            Date("funded_date"),
            Text("status"),
        ], ["loan_id"]),

        new Table("payment_schedule",
        [
            Int("loan_id"),
            Small("period_no"),
            Date("due_date"),
            Dec("payment_amt", 2),
            Dec("interest_amt", 2),
            Dec("principal_amt", 2),
            Dec("balance_after", 2),
        ], ["loan_id", "period_no"]),

        new Table("payment",
        [
            Int("payment_id"),
            Int("loan_id"),
            Small("period_no"),
            Date("paid_date"),
            Dec("amount", 2),
            Small("days_late"),
            Dec("late_fee", 2),
        ], ["payment_id"]),
    ];

    /// <summary>Sequence name -&gt; (owning table, id column, declared start).</summary>
    public static readonly IReadOnlyList<(string Sequence, string Table, string IdColumn, long Start)> Sequences =
    [
        ("seq_loan_application", "loan_application", "app_id", 1000),
        ("seq_loan", "loan", "loan_id", 5000),
        ("seq_payment", "payment", "payment_id", 90000),
    ];
}
