namespace Contoso.Lending.Migrator;

/// <summary>
/// Canonical token kind of a column, per the checksum spec (spec_version 1.0)
/// documented in the legacy repo's <c>database/checks/README.md</c>.
/// </summary>
internal enum ColumnKind
{
    /// <summary>NUMBER(p,0) / numeric(p) — digits only.</summary>
    Integer,

    /// <summary>NUMBER(p,2) / numeric(p,2) — exactly two decimals.</summary>
    Decimal2,

    /// <summary>NUMBER(p,3) / numeric(p,3) — exactly three decimals.</summary>
    Decimal3,

    /// <summary>NUMBER(p,4) / numeric(p,4) — exactly four decimals.</summary>
    Decimal4,

    /// <summary>Oracle DATE / Postgres timestamp(0).</summary>
    Date,

    /// <summary>VARCHAR2(n) / varchar(n) — verbatim characters.</summary>
    Text,
}

internal sealed record Column(string Name, ColumnKind Kind)
{
    public bool IsNumeric =>
        Kind is ColumnKind.Integer or ColumnKind.Decimal2 or ColumnKind.Decimal3 or ColumnKind.Decimal4;

    /// <summary>
    /// The TO_CHAR mask for this column's scale. Identical on Oracle and Postgres,
    /// which is what makes the two sides comparable as strings.
    /// </summary>
    public string Mask => Kind switch
    {
        ColumnKind.Integer => "FM99999999999999999990",
        ColumnKind.Decimal2 => "FM99999999999999999990.00",
        ColumnKind.Decimal3 => "FM99999999999999999990.000",
        ColumnKind.Decimal4 => "FM99999999999999999990.0000",
        _ => throw new InvalidOperationException($"column {Name} has no numeric mask"),
    };
}

/// <summary>
/// One legacy table: columns in declaration order (COLUMN_ID) and the primary-key
/// ordering that pins the hash chain.
/// </summary>
internal sealed record Table(string OracleName, string PostgresName, string[] PrimaryKey, Column[] Columns)
{
    public string OrderBy => string.Join(", ", PrimaryKey);

    public IEnumerable<Column> NumericColumns => Columns.Where(c => c.IsNumeric);

    public IEnumerable<Column> DateColumns => Columns.Where(c => c.Kind == ColumnKind.Date);

    public IEnumerable<Column> TextColumns => Columns.Where(c => c.Kind == ColumnKind.Text);
}

/// <summary>
/// The five tables of the LENDING schema, in foreign-key (insertable) order.
/// Column order is the declaration order of <c>database/schema.sql</c> in the
/// legacy repo, which the checksum spec requires.
/// </summary>
internal static class LendingSchema
{
    public static readonly Table[] Tables =
    [
        new Table("BORROWER", "borrower", ["BORROWER_ID"],
        [
            new Column("BORROWER_ID", ColumnKind.Integer),
            new Column("LEGAL_NAME", ColumnKind.Text),
            new Column("TAX_ID", ColumnKind.Text),
            new Column("CREDIT_SCORE", ColumnKind.Integer),
            new Column("DEPOSIT_BALANCE", ColumnKind.Decimal2),
            new Column("YEARS_IN_BUSINESS", ColumnKind.Integer),
            new Column("CREATED_AT", ColumnKind.Date),
        ]),
        new Table("LOAN_APPLICATION", "loan_application", ["APP_ID"],
        [
            new Column("APP_ID", ColumnKind.Integer),
            new Column("BORROWER_ID", ColumnKind.Integer),
            new Column("PRODUCT_TYPE", ColumnKind.Text),
            new Column("AMOUNT", ColumnKind.Decimal2),
            new Column("TERM_MONTHS", ColumnKind.Integer),
            new Column("CREDIT_SCORE", ColumnKind.Integer),
            new Column("DTI", ColumnKind.Decimal4),
            new Column("LTV", ColumnKind.Decimal4),
            new Column("STATUS", ColumnKind.Text),
            new Column("CREATED_AT", ColumnKind.Date),
        ]),
        new Table("LOAN", "loan", ["LOAN_ID"],
        [
            new Column("LOAN_ID", ColumnKind.Integer),
            new Column("APP_ID", ColumnKind.Integer),
            new Column("BORROWER_ID", ColumnKind.Integer),
            new Column("PRODUCT_TYPE", ColumnKind.Text),
            new Column("PRINCIPAL", ColumnKind.Decimal2),
            new Column("ANNUAL_RATE", ColumnKind.Decimal3),
            new Column("TERM_MONTHS", ColumnKind.Integer),
            new Column("ORIG_FEE", ColumnKind.Decimal2),
            new Column("FUNDED_DATE", ColumnKind.Date),
            new Column("STATUS", ColumnKind.Text),
        ]),
        new Table("PAYMENT_SCHEDULE", "payment_schedule", ["LOAN_ID", "PERIOD_NO"],
        [
            new Column("LOAN_ID", ColumnKind.Integer),
            new Column("PERIOD_NO", ColumnKind.Integer),
            new Column("DUE_DATE", ColumnKind.Date),
            new Column("PAYMENT_AMT", ColumnKind.Decimal2),
            new Column("INTEREST_AMT", ColumnKind.Decimal2),
            new Column("PRINCIPAL_AMT", ColumnKind.Decimal2),
            new Column("BALANCE_AFTER", ColumnKind.Decimal2),
        ]),
        new Table("PAYMENT", "payment", ["PAYMENT_ID"],
        [
            new Column("PAYMENT_ID", ColumnKind.Integer),
            new Column("LOAN_ID", ColumnKind.Integer),
            new Column("PERIOD_NO", ColumnKind.Integer),
            new Column("PAID_DATE", ColumnKind.Date),
            new Column("AMOUNT", ColumnKind.Decimal2),
            new Column("DAYS_LATE", ColumnKind.Integer),
            new Column("LATE_FEE", ColumnKind.Decimal2),
        ]),
    ];

    /// <summary>Oracle sequence names, carried over to Postgres in lower case.</summary>
    public static readonly string[] Sequences = ["SEQ_LOAN_APPLICATION", "SEQ_LOAN", "SEQ_PAYMENT"];
}
