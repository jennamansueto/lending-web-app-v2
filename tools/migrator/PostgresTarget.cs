using Npgsql;
using NpgsqlTypes;

namespace Contoso.Lending.Migrator;

internal sealed class PostgresTarget(string connectionString) : IDisposable
{
    private readonly NpgsqlConnection _conn = new(connectionString);

    public void Open()
    {
        _conn.Open();
        // The canonical encoding must never depend on session settings.
        Execute("SET lc_numeric = 'C'; SET DateStyle = 'ISO, YMD'; SET TimeZone = 'UTC';");
    }

    public void Dispose() => _conn.Dispose();

    public void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void TruncateAll()
    {
        var tables = string.Join(", ", Schema.Tables.Reverse().Select(t => t.Name));
        Execute($"TRUNCATE TABLE {tables}");
    }

    public long Copy(Table table, IEnumerable<object?[]> rows)
    {
        var columns = string.Join(", ", table.Columns.Select(c => c.Name));
        using var writer = _conn.BeginBinaryImport($"COPY {table.Name} ({columns}) FROM STDIN (FORMAT BINARY)");
        long written = 0;
        foreach (var row in rows)
        {
            writer.StartRow();
            for (var i = 0; i < table.Columns.Count; i++)
            {
                var column = table.Columns[i];
                var value = row[i];
                if (value is null)
                {
                    writer.WriteNull();
                    continue;
                }

                switch (column.PgType)
                {
                    case "integer":
                        writer.Write(decimal.ToInt32((decimal)value), NpgsqlDbType.Integer);
                        break;
                    case "smallint":
                        writer.Write(decimal.ToInt16((decimal)value), NpgsqlDbType.Smallint);
                        break;
                    case "numeric":
                        writer.Write((decimal)value, NpgsqlDbType.Numeric);
                        break;
                    case "timestamp":
                        writer.Write((DateTime)value, NpgsqlDbType.Timestamp);
                        break;
                    default:
                        writer.Write((string)value, NpgsqlDbType.Varchar);
                        break;
                }
            }

            written++;
        }

        writer.Complete();
        return written;
    }

    /// <summary>
    /// Points every sequence at <c>max(id) + 1</c> (or its declared start value
    /// when the owning table is empty), so the next <c>nextval</c> hands out a
    /// free id. Idempotent: a repeated migration lands on the same value.
    /// </summary>
    public IReadOnlyList<(string Sequence, long Next)> ResetSequences()
    {
        var result = new List<(string, long)>();
        foreach (var (sequence, table, idColumn, start) in Schema.Sequences)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT max({idColumn}) FROM {table}";
            var max = cmd.ExecuteScalar();
            var next = max is null or DBNull ? start : Convert.ToInt64(max) + 1;

            using var setval = _conn.CreateCommand();
            setval.CommandText = $"SELECT setval('{sequence}', {next}, false)";
            setval.ExecuteNonQuery();
            result.Add((sequence, next));
        }

        return result;
    }

    public IEnumerable<string> ReadCanonicalRows(Table table)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = CanonicalSql.PostgresRowQuery(table);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return reader.GetString(0);
        }
    }

    public long RowCount(Table table)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = CanonicalSql.PostgresCountQuery(table);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public IReadOnlyDictionary<string, string> ColumnSums(Table table)
    {
        var sums = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = CanonicalSql.PostgresSumQuery(table);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            sums[reader.GetString(0)] = reader.GetString(1);
        }

        return sums;
    }
}
