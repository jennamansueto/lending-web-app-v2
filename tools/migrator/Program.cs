using System.Diagnostics;

namespace Contoso.Lending.Migrator;

/// <summary>
/// Oracle -> Postgres data-layer CLI for the LENDING schema.
///
///   migrate  truncate the Postgres tables, copy every Oracle row, reset every
///            sequence to its legacy position. Re-runnable: two consecutive runs
///            leave an identical database.
///   verify   compare Oracle and Postgres exactly — row counts, per-numeric-column
///            SUM/MIN/MAX, per-date MIN/MAX and the spec_version 1.0 hash chains —
///            and compare the Postgres side against the committed Phase-1 baseline.
///            Exits non-zero on any mismatch. There are no tolerances anywhere.
/// </summary>
internal static class Program
{
    private const string DefaultOracle =
        "User Id=lending;Password=lending_pw_2014;Data Source=localhost:1521/FREEPDB1";

    private const string DefaultPostgres =
        "Host=localhost;Port=5432;Database=lending;Username=lending;Password=lending_pw_2014";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: Contoso.Lending.Migrator <migrate|verify>");
            return 2;
        }

        var command = args[0].ToLowerInvariant();
        string? reportPath = ReadOption(args, "--report");
        var oracleConnectionString =
            Environment.GetEnvironmentVariable("MIGRATOR_ORACLE") ?? DefaultOracle;
        var postgresConnectionString =
            Environment.GetEnvironmentVariable("MIGRATOR_POSTGRES") ?? DefaultPostgres;
        var repoRoot = RepositoryRoot();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return command switch
            {
                "migrate" => await MigrateAsync(oracleConnectionString, postgresConnectionString, cts.Token),
                "verify" => await VerifyAsync(
                    oracleConnectionString, postgresConnectionString, repoRoot, reportPath, cts.Token),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.Message}");
            return 3;
        }
    }

    private static string? ReadOption(string[] args, string option)
    {
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == option)
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"{option} requires a path");
                }

                return args[i + 1];
            }
        }

        return null;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'; expected migrate or verify");
        return 2;
    }

    private static async Task<int> MigrateAsync(string oracle, string postgres, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var source = await OracleSide.ConnectAsync(oracle, ct);
        await using var target = await PostgresSide.ConnectAsync(postgres, ct);

        Console.WriteLine($"source : {await source.VersionAsync(ct)}");
        Console.WriteLine($"target : {await target.VersionAsync(ct)}");
        Console.WriteLine();

        await target.TruncateAllAsync(ct);
        Console.WriteLine($"truncated: {string.Join(", ", LendingSchema.Tables.Select(t => t.PostgresName))}");

        foreach (var table in LendingSchema.Tables)
        {
            var copied = await target.CopyAsync(table, source.ReadRowsAsync(table, ct), ct);
            Console.WriteLine($"copied   : {table.PostgresName,-17} {copied,6} rows");
        }

        Console.WriteLine();
        var oraclePositions = await source.SequencePositionsAsync(ct);
        foreach (var sequence in LendingSchema.Sequences)
        {
            if (!oraclePositions.TryGetValue(sequence, out var position))
            {
                throw new InvalidOperationException($"Oracle has no sequence {sequence}");
            }

            await target.SetSequenceAsync(sequence, position, ct);
            Console.WriteLine(
                $"sequence : {sequence.ToLowerInvariant(),-21} next nextval() = {position} (legacy LAST_NUMBER)");
        }

        Console.WriteLine();
        Console.WriteLine($"migrate OK in {stopwatch.Elapsed.TotalSeconds:F1}s — run `verify` to prove parity.");
        return 0;
    }

    private static async Task<int> VerifyAsync(
        string oracle, string postgres, string repoRoot, string? reportPath, CancellationToken ct)
    {
        var harness = Path.Combine(repoRoot, "database", "checks", "checksum_postgres.sql");
        var baselinePath = Path.Combine(repoRoot, "database", "checks", "baseline-oracle.json");
        foreach (var path in new[] { harness, baselinePath })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"required file not found: {path}");
            }
        }

        await using var source = await OracleSide.ConnectAsync(oracle, ct);
        await using var target = await PostgresSide.ConnectAsync(postgres, ct);

        Console.WriteLine($"oracle   : {await source.VersionAsync(ct)}");
        Console.WriteLine($"postgres : {await target.VersionAsync(ct)}");
        Console.WriteLine($"harness  : {harness}");
        Console.WriteLine($"baseline : {baselinePath}");
        Console.WriteLine();

        var baseline = Baseline.Load(baselinePath);
        var postgresChecksums = await target.ChecksumsAsync(harness, ct);
        var mismatches = new List<string>();
        var reportRows = new List<VerifyRow>();

        foreach (var table in LendingSchema.Tables)
        {
            var oracleChecksum = await source.ChecksumAsync(table, ct);
            if (!postgresChecksums.TryGetValue(table.OracleName, out var postgresChecksum))
            {
                mismatches.Add($"{table.OracleName}: the checksum harness reported no result");
                continue;
            }

            await target.AddDateAndCollisionsAsync(table, postgresChecksum, ct);
            var baselineTable = baseline.Tables[table.OracleName];
            var tableMismatches = new List<string>();

            void Check(string what, string oracleValue, string postgresValue, string? baselineValue = null)
            {
                bool equal = string.Equals(oracleValue, postgresValue, StringComparison.Ordinal)
                    && (baselineValue is null
                        || string.Equals(baselineValue, postgresValue, StringComparison.Ordinal));
                reportRows.Add(new VerifyRow(
                    table.PostgresName,
                    what,
                    oracleValue,
                    postgresValue,
                    equal ? "pass" : "fail",
                    baselineValue));
                if (!equal)
                {
                    tableMismatches.Add(
                        $"{table.OracleName}.{what}: oracle={oracleValue} baseline={baselineValue ?? "(not checked)"} postgres={postgresValue}");
                }
            }

            Check(
                "row_count",
                oracleChecksum.RowCount.ToString(),
                postgresChecksum.RowCount.ToString(),
                baselineTable.RowCount.ToString());

            foreach (var column in table.NumericColumns)
            {
                Check(
                    $"SUM({column.Name})",
                    oracleChecksum.NumericSums[column.Name],
                    postgresChecksum.NumericSums[column.Name],
                    baselineTable.NumericSums[column.Name]);
                Check($"MIN({column.Name})", oracleChecksum.NumericMin[column.Name], postgresChecksum.NumericMin[column.Name]);
                Check($"MAX({column.Name})", oracleChecksum.NumericMax[column.Name], postgresChecksum.NumericMax[column.Name]);
            }

            foreach (var column in table.DateColumns)
            {
                Check($"MIN({column.Name})", oracleChecksum.DateMin[column.Name], postgresChecksum.DateMin[column.Name]);
                Check($"MAX({column.Name})", oracleChecksum.DateMax[column.Name], postgresChecksum.DateMax[column.Name]);
            }

            Check("row_hash_chain", oracleChecksum.RowHashChain, postgresChecksum.RowHashChain, baselineTable.RowHashChain);
            Check("delimiter_collisions",
                oracleChecksum.DelimiterCollisions.ToString(),
                target.Collisions[table.OracleName].ToString(),
                baselineTable.DelimiterCollisions.ToString());

            mismatches.AddRange(tableMismatches);
            Console.WriteLine(
                $"{(tableMismatches.Count == 0 ? "OK  " : "FAIL")} {table.OracleName,-17} " +
                $"rows={postgresChecksum.RowCount,4}  chain={postgresChecksum.RowHashChain}");

            foreach (var column in table.NumericColumns)
            {
                Console.WriteLine(
                    $"       {column.Name} SUM = {postgresChecksum.NumericSums[column.Name]}  " +
                    $"MIN = {postgresChecksum.NumericMin[column.Name]}  " +
                    $"MAX = {postgresChecksum.NumericMax[column.Name]}");
            }

            foreach (var column in table.DateColumns)
            {
                Console.WriteLine(
                    $"       {column.Name} MIN = {postgresChecksum.DateMin[column.Name]}  " +
                    $"MAX = {postgresChecksum.DateMax[column.Name]}");
            }
        }

        Console.WriteLine();
        var oracleSequences = await source.SequencePositionsAsync(ct);
        var postgresSequences = await target.SequencePositionsAsync(ct);
        foreach (var sequence in LendingSchema.Sequences)
        {
            var oraclePosition = oracleSequences[sequence];
            var baselinePosition = baseline.Sequences[sequence];
            var postgresPosition = postgresSequences[sequence];
            var ok = oraclePosition == postgresPosition && baselinePosition == postgresPosition;
            reportRows.Add(new VerifyRow(
                "sequence",
                sequence.ToLowerInvariant(),
                oraclePosition.ToString(),
                postgresPosition.ToString(),
                ok ? "pass" : "fail",
                baselinePosition.ToString()));
            if (!ok)
            {
                mismatches.Add(
                    $"{sequence}: oracle={oraclePosition} baseline={baselinePosition} postgres={postgresPosition}");
            }

            Console.WriteLine(
                $"{(ok ? "OK  " : "FAIL")} {sequence.ToLowerInvariant(),-21} " +
                $"position={postgresPosition} (oracle={oraclePosition}, baseline={baselinePosition})");
        }

        if (reportPath is not null)
        {
            new VerifyReport(reportRows).Write(reportPath);
        }

        Console.WriteLine();
        if (mismatches.Count > 0)
        {
            Console.Error.WriteLine($"verify FAILED with {mismatches.Count} mismatch(es):");
            foreach (var mismatch in mismatches)
            {
                Console.Error.WriteLine($"  - {mismatch}");
            }

            return 1;
        }

        Console.WriteLine(
            $"verify OK — spec_version {baseline.SpecVersion}, zero mismatches across " +
            $"{LendingSchema.Tables.Length} tables and {LendingSchema.Sequences.Length} sequences.");
        return 0;
    }

    /// <summary>Walks up from the executable to the repository root (the directory holding <c>database/checks</c>).</summary>
    private static string RepositoryRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("MIGRATOR_REPO_ROOT");
        if (!string.IsNullOrEmpty(explicitRoot))
        {
            return explicitRoot;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "database", "checks")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "could not locate the repository root; set MIGRATOR_REPO_ROOT");
    }
}
