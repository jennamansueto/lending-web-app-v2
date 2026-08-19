using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Contoso.Lending.Migrator;

/// <summary>
/// The Postgres 16 target. Checksums are computed by executing the committed
/// <c>database/checks/checksum_postgres.sql</c> harness verbatim, so the two sides
/// of a verify are computed by the Phase-1 spec implementation, not by ad-hoc SQL.
/// </summary>
internal sealed class PostgresSide : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;

    private PostgresSide(NpgsqlConnection connection) => _connection = connection;

    public static async Task<PostgresSide> ConnectAsync(string connectionString, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using (var cmd = new NpgsqlCommand("SHOW server_encoding", connection))
        {
            var encoding = (string?)await cmd.ExecuteScalarAsync(ct);
            if (!string.Equals(encoding, "UTF8", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"server_encoding is {encoding}; the checksum spec requires UTF8");
            }
        }

        await using (var cmd = new NpgsqlCommand("SET standard_conforming_strings = on", connection))
        {
            await cmd.ExecuteNonQueryAsync(ct);
        }

        return new PostgresSide(connection);
    }

    public async Task<string> VersionAsync(CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT version()", _connection);
        return (string?)await cmd.ExecuteScalarAsync(ct) ?? "unknown";
    }

    /// <summary>Empties every table in one statement, so foreign keys never block the reload.</summary>
    public async Task TruncateAllAsync(CancellationToken ct)
    {
        var tables = string.Join(", ", LendingSchema.Tables.Select(t => t.PostgresName));
        await using var cmd = new NpgsqlCommand($"TRUNCATE TABLE {tables}", _connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Bulk-loads rows with binary COPY, preserving numeric scale exactly.</summary>
    public async Task<long> CopyAsync(Table table, IAsyncEnumerable<object?[]> rows, CancellationToken ct)
    {
        var columns = string.Join(", ", table.Columns.Select(c => c.Name.ToLowerInvariant()));
        await using var writer = await _connection.BeginBinaryImportAsync(
            $"COPY {table.PostgresName} ({columns}) FROM STDIN (FORMAT BINARY)", ct);

        long copied = 0;
        await foreach (var row in rows.WithCancellation(ct))
        {
            await writer.StartRowAsync(ct);
            for (var i = 0; i < row.Length; i++)
            {
                var value = row[i];
                if (value is null)
                {
                    await writer.WriteNullAsync(ct);
                    continue;
                }

                switch (table.Columns[i].Kind)
                {
                    case ColumnKind.Date:
                        await writer.WriteAsync((DateTime)value, NpgsqlDbType.Timestamp, ct);
                        break;
                    case ColumnKind.Text:
                        await writer.WriteAsync((string)value, NpgsqlDbType.Varchar, ct);
                        break;
                    default:
                        await writer.WriteAsync((decimal)value, NpgsqlDbType.Numeric, ct);
                        break;
                }
            }

            copied++;
        }

        await writer.CompleteAsync(ct);
        return copied;
    }

    /// <summary>
    /// Positions a sequence so that the next <c>nextval()</c> returns <paramref name="position"/>,
    /// which is what Oracle's <c>LAST_NUMBER</c> means for a sequence with an unconsumed cache.
    /// </summary>
    public async Task SetSequenceAsync(string oracleName, long position, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT setval($1, $2, false)", _connection);
        cmd.Parameters.AddWithValue(oracleName.ToLowerInvariant());
        cmd.Parameters.AddWithValue(position);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Dictionary<string, long>> SequencePositionsAsync(CancellationToken ct)
    {
        var positions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var sequence in LendingSchema.Sequences)
        {
            var name = sequence.ToLowerInvariant();

            await using (var incrementCmd = new NpgsqlCommand(
                             "SELECT increment_by FROM pg_sequences " +
                             "WHERE schemaname = current_schema() AND sequencename = $1", _connection))
            {
                incrementCmd.Parameters.AddWithValue(name);
                var increment = (long?)await incrementCmd.ExecuteScalarAsync(ct)
                                ?? throw new InvalidOperationException($"sequence {name} is missing");
                if (increment != 1)
                {
                    throw new InvalidOperationException(
                        $"sequence {name} has INCREMENT BY {increment}; expected 1");
                }
            }

            // last_value/is_called give the exact position: the next nextval() returns
            // last_value when is_called is false, last_value + 1 otherwise. That is the
            // same quantity Oracle reports as LAST_NUMBER for an unconsumed cache.
            await using var cmd = new NpgsqlCommand($"SELECT last_value, is_called FROM {name}", _connection);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                throw new InvalidOperationException($"sequence {name} returned no row");
            }

            var lastValue = reader.GetInt64(0);
            positions[sequence] = reader.GetBoolean(1) ? lastValue + 1 : lastValue;
        }

        return positions;
    }

    /// <summary>
    /// Runs the committed checksum harness and returns one entry per table
    /// (row count, canonical numeric sums and hash chain), keyed by table name.
    /// </summary>
    public async Task<Dictionary<string, TableChecksum>> ChecksumsAsync(string harnessSqlPath, CancellationToken ct)
    {
        var results = new Dictionary<string, TableChecksum>(StringComparer.OrdinalIgnoreCase);

        foreach (var statement in SplitStatements(await File.ReadAllTextAsync(harnessSqlPath, ct)))
        {
            await using var cmd = new NpgsqlCommand(statement, _connection);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
            {
                throw new InvalidOperationException("checksum harness returned no row for a statement");
            }

            var tableName = reader.GetString(reader.GetOrdinal("table"));
            var checksum = new TableChecksum
            {
                Table = tableName,
                RowCount = reader.GetInt64(reader.GetOrdinal("row_count")),
                RowHashChain = reader.GetString(reader.GetOrdinal("row_hash_chain")),
            };

            for (var i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                if (!name.StartsWith("sum_", StringComparison.Ordinal))
                {
                    continue;
                }

                checksum.NumericSums[name["sum_".Length..].ToUpperInvariant()] =
                    reader.IsDBNull(i) ? "\\N" : reader.GetString(i);
            }

            results[tableName] = checksum;
        }

        if (results.Count != LendingSchema.Tables.Length)
        {
            throw new InvalidOperationException(
                $"checksum harness reported {results.Count} tables, expected {LendingSchema.Tables.Length}");
        }

        return results;
    }

    /// <summary>
    /// Numeric MIN/MAX, date MIN/MAX and the delimiter-collision count, which the
    /// checksum harness does not report. Same canonical masks as the Oracle side.
    /// </summary>
    public async Task AddDateAndCollisionsAsync(Table table, TableChecksum checksum, CancellationToken ct)
    {
        var numericColumns = table.NumericColumns.ToArray();
        var dateColumns = table.DateColumns.ToArray();
        var selects = new List<string>();
        foreach (var column in numericColumns)
        {
            var name = column.Name.ToLowerInvariant();
            selects.Add($"coalesce(to_char(min({name}), '{column.Mask}'), '\\N')");
            selects.Add($"coalesce(to_char(max({name}), '{column.Mask}'), '\\N')");
        }

        foreach (var column in dateColumns)
        {
            var name = column.Name.ToLowerInvariant();
            selects.Add($"coalesce(to_char(min({name}), 'YYYY-MM-DD HH24:MI:SS'), '\\N')");
            selects.Add($"coalesce(to_char(max({name}), 'YYYY-MM-DD HH24:MI:SS'), '\\N')");
        }

        selects.Add(CollisionExpression(table));

        await using var cmd = new NpgsqlCommand(
            $"SELECT {string.Join(", ", selects)} FROM {table.PostgresName}", _connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException($"no aggregate row for {table.PostgresName}");
        }

        var field = 0;
        foreach (var column in numericColumns)
        {
            checksum.NumericMin[column.Name] = reader.GetString(field++);
            checksum.NumericMax[column.Name] = reader.GetString(field++);
        }

        foreach (var column in dateColumns)
        {
            checksum.DateMin[column.Name] = reader.GetString(field++);
            checksum.DateMax[column.Name] = reader.GetString(field++);
        }

        Collisions[table.OracleName] = reader.GetInt64(field);
    }

    /// <summary>Delimiter collisions per table, populated by <see cref="AddDateAndCollisionsAsync"/>.</summary>
    public Dictionary<string, long> Collisions { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static string CollisionExpression(Table table)
    {
        var textColumns = table.TextColumns.ToArray();
        if (textColumns.Length == 0)
        {
            return "0::bigint";
        }

        var predicate = string.Join(" OR ", textColumns.SelectMany(c => new[]
        {
            $"position('|' in {c.Name.ToLowerInvariant()}) > 0",
            $"position('\\N' in {c.Name.ToLowerInvariant()}) > 0",
        }));

        return $"count(CASE WHEN {predicate} THEN 1 END)";
    }

    /// <summary>
    /// Splits the harness file into executable statements: psql meta-commands
    /// (lines starting with a backslash) are dropped, everything else is split on
    /// statement-terminating semicolons.
    /// </summary>
    internal static IEnumerable<string> SplitStatements(string sql)
    {
        var current = new StringBuilder();
        foreach (var rawLine in sql.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.TrimStart().StartsWith('\\'))
            {
                continue;
            }

            current.Append(line).Append('\n');
            if (line.TrimEnd().EndsWith(';'))
            {
                var statement = current.ToString().Trim();
                current.Clear();
                if (statement.Length > 1)
                {
                    yield return statement;
                }
            }
        }

        var tail = current.ToString().Trim();
        if (tail.Length > 0 && !tail.StartsWith("--", StringComparison.Ordinal))
        {
            yield return tail;
        }
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
