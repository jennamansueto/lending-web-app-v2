using System.Security.Cryptography;
using System.Text;

namespace Contoso.Lending.Migrator;

/// <summary>Per-table comparison payload. Every value is a string: scale is part of the check.</summary>
internal sealed class TableChecksum
{
    public required string Table { get; init; }

    public required long RowCount { get; init; }

    /// <summary>Column name -> canonical SUM token (or <c>\N</c> when no non-null rows).</summary>
    public Dictionary<string, string> NumericSums { get; } = [];

    /// <summary>Column name -> canonical numeric MIN token.</summary>
    public Dictionary<string, string> NumericMin { get; } = [];

    /// <summary>Column name -> canonical numeric MAX token.</summary>
    public Dictionary<string, string> NumericMax { get; } = [];

    /// <summary>Column name -> canonical date MIN token.</summary>
    public Dictionary<string, string> DateMin { get; } = [];

    /// <summary>Column name -> canonical date MAX token.</summary>
    public Dictionary<string, string> DateMax { get; } = [];

    /// <summary>Uppercase-hex SHA-256 hash chain over the rows, ordered by primary key.</summary>
    public required string RowHashChain { get; init; }

    /// <summary>Rows whose text columns contain <c>|</c> or the two characters <c>\N</c>. Must be 0.</summary>
    public long DelimiterCollisions { get; init; }
}

internal static class HashChain
{
    public const string Seed = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// Folds per-row hashes into the table chain exactly as the checksum spec
    /// defines it: acc[k] = SHA-256(text(acc[k-1] || row_hash[k])), uppercase hex.
    /// </summary>
    public static string Fold(IEnumerable<string> rowHashes)
    {
        var acc = Seed;
        foreach (var rowHash in rowHashes)
        {
            acc = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(acc + rowHash)));
        }

        return acc;
    }
}
