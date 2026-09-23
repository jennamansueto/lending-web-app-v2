using Oracle.ManagedDataAccess.Client;

namespace Contoso.Lending.Migrator;

/// <summary>Read-only access to the legacy Oracle system of record.</summary>
internal sealed class OracleSource(string connectionString) : IDisposable
{
    private readonly OracleConnection _conn = new(connectionString);

    public void Open() => _conn.Open();

    public void Dispose() => _conn.Dispose();

    /// <summary>Rows in primary-key order, values converted without going through a binary float.</summary>
    public IEnumerable<object?[]> ReadRows(Table table)
    {
        var columns = string.Join(", ", table.Columns.Select(c => c.Name.ToUpperInvariant()));
        var order = string.Join(", ", table.PrimaryKey.Select(k => k.ToUpperInvariant()));
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT {columns} FROM {table.Name.ToUpperInvariant()} ORDER BY {order}";
        cmd.FetchSize = 1 << 20;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var values = new object?[table.Columns.Count];
            for (var i = 0; i < table.Columns.Count; i++)
            {
                if (reader.IsDBNull(i))
                {
                    values[i] = null;
                    continue;
                }

                values[i] = table.Columns[i].Kind switch
                {
                    ColumnKind.Integer or ColumnKind.Decimal => reader.GetOracleDecimal(i).Value,
                    ColumnKind.Date => reader.GetDateTime(i),
                    _ => reader.GetString(i),
                };
            }

            yield return values;
        }
    }

    public IEnumerable<string> ReadCanonicalRows(Table table)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = CanonicalSql.OracleRowQuery(table);
        cmd.FetchSize = 1 << 20;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            yield return reader.GetString(0);
        }
    }

    public long RowCount(Table table)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = CanonicalSql.OracleCountQuery(table);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public IReadOnlyDictionary<string, string> ColumnSums(Table table)
    {
        var sums = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = CanonicalSql.OracleSumQuery(table);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            sums[reader.GetString(0)] = reader.GetString(1);
        }

        return sums;
    }
}
