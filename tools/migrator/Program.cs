using System.Diagnostics;
using System.Globalization;
using Contoso.Lending.Migrator;

const string DefaultOracleConn =
    "User Id=lending;Password=lending_pw_2014;Data Source=localhost:1521/FREEPDB1;";
const string DefaultPostgresConn =
    "Host=localhost;Port=5432;Database=lending;Username=lending;Password=lending_pw_2014";

var oracleConn = Environment.GetEnvironmentVariable("ORACLE_CONN") is { Length: > 0 } o ? o : DefaultOracleConn;
var postgresConn = Environment.GetEnvironmentVariable("POSTGRES_CONN") is { Length: > 0 } p ? p : DefaultPostgresConn;

var command = args.Length > 0 ? args[0] : "help";

return command switch
{
    "migrate" => Migrate(),
    "verify" => Verify(),
    _ => Help(),
};

int Help()
{
    Console.WriteLine("""
        Contoso Commercial Lending — Oracle -> Postgres data migrator

        usage: migrator <command>

          migrate   truncate the Postgres tables, copy every row from Oracle and
                    reset the sequences. Re-runnable: identical result every time.
          verify    compare row counts, per-numeric-column sums and the legacy
                    text/date checksums on both sides with zero tolerance, write
                    parity/reports/data-parity.json and exit non-zero on mismatch.

        environment:
          ORACLE_CONN     default: {DefaultOracleConn}
          POSTGRES_CONN   default: {DefaultPostgresConn}
        """.Replace("{DefaultOracleConn}", DefaultOracleConn)
           .Replace("{DefaultPostgresConn}", DefaultPostgresConn));
    return args.Length > 0 && args[0] is "help" or "--help" or "-h" ? 0 : 2;
}

int Migrate()
{
    var stopwatch = Stopwatch.StartNew();
    using var oracle = new OracleSource(oracleConn);
    using var postgres = new PostgresTarget(postgresConn);
    oracle.Open();
    postgres.Open();

    Console.WriteLine("migrate: Oracle -> Postgres");
    postgres.TruncateAll();
    Console.WriteLine("  truncated all target tables");

    foreach (var table in Schema.Tables)
    {
        var copied = postgres.Copy(table, oracle.ReadRows(table));
        Console.WriteLine($"  {table.Name,-18} {copied,7} rows copied");
    }

    foreach (var (sequence, next) in postgres.ResetSequences())
    {
        Console.WriteLine($"  {sequence,-22} next value {next}");
    }

    Console.WriteLine($"migrate: done in {stopwatch.Elapsed.TotalSeconds:F1}s");
    return 0;
}

int Verify()
{
    using var oracle = new OracleSource(oracleConn);
    using var postgres = new PostgresTarget(postgresConn);
    oracle.Open();
    postgres.Open();

    var results = new List<CheckResult>();
    Console.WriteLine("verify: Oracle vs Postgres (zero tolerance)");

    foreach (var table in Schema.Tables)
    {
        Console.WriteLine($"  {table.Name}");

        Add(table.Name, "row_count",
            oracle.RowCount(table).ToString(CultureInfo.InvariantCulture),
            postgres.RowCount(table).ToString(CultureInfo.InvariantCulture));

        var oracleSums = oracle.ColumnSums(table);
        var postgresSums = postgres.ColumnSums(table);
        foreach (var column in table.NumericColumns)
        {
            Add(table.Name, $"sum:{column.Name}",
                oracleSums.GetValueOrDefault(column.Name, "<missing>"),
                postgresSums.GetValueOrDefault(column.Name, "<missing>"));
        }

        var oracleChecksum = Fold(oracle.ReadCanonicalRows(table));
        var postgresChecksum = Fold(postgres.ReadCanonicalRows(table));
        Add(table.Name, "row_hash_sum", oracleChecksum.RowHashSum, postgresChecksum.RowHashSum);
        Add(table.Name, "table_hash", oracleChecksum.TableHash, postgresChecksum.TableHash);
    }

    var failed = results.Count(r => !r.Pass);
    var reportPath = Report.Write(results);
    Console.WriteLine();
    Console.WriteLine($"verify: {results.Count - failed}/{results.Count} checks passed, {failed} failed");
    Console.WriteLine($"verify: report written to {reportPath}");
    return failed == 0 ? 0 : 1;

    void Add(string table, string check, string oracleValue, string postgresValue)
    {
        var result = new CheckResult(table, check, oracleValue, postgresValue,
            string.Equals(oracleValue, postgresValue, StringComparison.Ordinal));
        results.Add(result);
        var status = result.Pass ? "PASS" : "FAIL";
        Console.WriteLine($"    [{status}] {check,-22} oracle={oracleValue} postgres={postgresValue}");
    }

    static Checksum Fold(IEnumerable<string> rows)
    {
        var checksum = new Checksum();
        foreach (var row in rows)
        {
            checksum.Add(row);
        }

        return checksum;
    }
}
