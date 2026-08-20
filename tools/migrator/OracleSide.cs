using Oracle.ManagedDataAccess.Client;

namespace Contoso.Lending.Migrator;

/// <summary>
/// Read-only access to the legacy Oracle system of record. Issues nothing but
/// SELECTs and the deterministic ALTER SESSION settings the checksum spec pins.
/// </summary>
internal sealed class OracleSide : IAsyncDisposable
{
    private readonly OracleConnection _connection;

    private OracleSide(OracleConnection connection) => _connection = connection;

    public static async Task<OracleSide> ConnectAsync(string connectionString, CancellationToken ct)
    {
        var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(ct);

        // Canonical tokens must not depend on the caller's NLS environment
        // (checksum spec §7).
        foreach (var setting in new[]
                 {
                     "ALTER SESSION SET NLS_NUMERIC_CHARACTERS = '.,'",
                     "ALTER SESSION SET NLS_DATE_FORMAT = 'YYYY-MM-DD HH24:MI:SS'",
                     "ALTER SESSION SET NLS_SORT = 'BINARY'",
                     "ALTER SESSION SET NLS_COMP = 'BINARY'",
                 })
        {
            await using var cmd = new OracleCommand(setting, connection);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return new OracleSide(connection);
    }

    public async Task<string> VersionAsync(CancellationToken ct)
    {
        await using var cmd = new OracleCommand("SELECT banner_full FROM v$version", _connection);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value?.ToString()?.Split('\n')[0] ?? "unknown";
    }

    /// <summary>Streams every row of a table ordered by its primary key.</summary>
    public async IAsyncEnumerable<object?[]> ReadRowsAsync(
        Table table,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var columns = string.Join(", ", table.Columns.Select(c => c.Name));
        var sql = $"SELECT {columns} FROM {table.OracleName} ORDER BY {table.OrderBy}";

        await using var cmd = new OracleCommand(sql, _connection) { FetchSize = 1 << 20 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var values = new object?[table.Columns.Length];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = await reader.IsDBNullAsync(i, ct)
                    ? null
                    : table.Columns[i].Kind == ColumnKind.Date
                        ? reader.GetDateTime(i)
                        : table.Columns[i].IsNumeric
                            ? reader.GetDecimal(i)
                            : reader.GetString(i);
            }

            yield return values;
        }
    }

    public async Task<TableChecksum> ChecksumAsync(Table table, CancellationToken ct)
    {
        var rowExpression = CanonicalRowExpression(table);

        var rowHashes = new List<string>();
        await using (var cmd = new OracleCommand(
                         $"SELECT RAWTOHEX(STANDARD_HASH({rowExpression}, 'SHA256')) FROM {table.OracleName} " +
                         $"ORDER BY {table.OrderBy}", _connection))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rowHashes.Add(reader.GetString(0));
            }
        }

        var aggregates = new List<string> { "COUNT(*)" };
        var numericColumns = table.NumericColumns.ToArray();
        var dateColumns = table.DateColumns.ToArray();
        foreach (var column in numericColumns)
        {
            aggregates.Add($"NVL(TO_CHAR(SUM({column.Name}), '{column.Mask}'), '\\N')");
            aggregates.Add($"NVL(TO_CHAR(MIN({column.Name}), '{column.Mask}'), '\\N')");
            aggregates.Add($"NVL(TO_CHAR(MAX({column.Name}), '{column.Mask}'), '\\N')");
        }

        foreach (var column in dateColumns)
        {
            aggregates.Add($"NVL(TO_CHAR(MIN({column.Name}), 'YYYY-MM-DD HH24:MI:SS'), '\\N')");
            aggregates.Add($"NVL(TO_CHAR(MAX({column.Name}), 'YYYY-MM-DD HH24:MI:SS'), '\\N')");
        }

        aggregates.Add(CollisionExpression(table));

        await using var aggCmd = new OracleCommand(
            $"SELECT {string.Join(", ", aggregates)} FROM {table.OracleName}", _connection);
        await using var aggReader = await aggCmd.ExecuteReaderAsync(ct);
        if (!await aggReader.ReadAsync(ct))
        {
            throw new InvalidOperationException($"no aggregate row for {table.OracleName}");
        }

        var checksum = new TableChecksum
        {
            Table = table.OracleName,
            RowCount = Convert.ToInt64(aggReader.GetDecimal(0)),
            RowHashChain = HashChain.Fold(rowHashes),
            DelimiterCollisions = Convert.ToInt64(
                aggReader.GetDecimal(1 + (numericColumns.Length * 3) + (dateColumns.Length * 2))),
        };

        var field = 1;
        foreach (var column in numericColumns)
        {
            checksum.NumericSums[column.Name] = aggReader.GetString(field++);
            checksum.NumericMin[column.Name] = aggReader.GetString(field++);
            checksum.NumericMax[column.Name] = aggReader.GetString(field++);
        }

        foreach (var column in dateColumns)
        {
            checksum.DateMin[column.Name] = aggReader.GetString(field++);
            checksum.DateMax[column.Name] = aggReader.GetString(field++);
        }

        return checksum;
    }

    public async Task<Dictionary<string, long>> SequencePositionsAsync(CancellationToken ct)
    {
        var positions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new OracleCommand(
            "SELECT sequence_name, last_number, increment_by FROM user_sequences ORDER BY sequence_name",
            _connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var increment = reader.GetDecimal(2);
            if (increment != 1m)
            {
                throw new InvalidOperationException(
                    $"sequence {reader.GetString(0)} has INCREMENT BY {increment}; the Postgres DDL assumes 1");
            }

            positions[reader.GetString(0)] = Convert.ToInt64(reader.GetDecimal(1));
        }

        return positions;
    }

    /// <summary>Canonical row string per the checksum spec: tokens joined with a single '|'.</summary>
    private static string CanonicalRowExpression(Table table) =>
        string.Join("||'|'||", table.Columns.Select(c => c.Kind switch
        {
            ColumnKind.Text => $"NVL({c.Name},'\\N')",
            ColumnKind.Date => $"NVL(TO_CHAR({c.Name},'YYYY-MM-DD HH24:MI:SS'),'\\N')",
            _ => $"NVL(TO_CHAR({c.Name},'{c.Mask}'),'\\N')",
        }));

    private static string CollisionExpression(Table table)
    {
        var textColumns = table.TextColumns.ToArray();
        if (textColumns.Length == 0)
        {
            return "0";
        }

        var predicate = string.Join(" OR ", textColumns.SelectMany(c => new[]
        {
            $"INSTR({c.Name}, '|') > 0",
            $"INSTR({c.Name}, '\\N') > 0",
        }));

        return $"COUNT(CASE WHEN {predicate} THEN 1 END)";
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
