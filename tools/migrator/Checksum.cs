using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Contoso.Lending.Migrator;

/// <summary>
/// The ordered SHA-256 fold and the order-independent row-hash sum, exactly as
/// specified by the legacy checksum contract (algorithm_version 1):
///
///   row_hash_i   = lowercase_hex(SHA256(utf8(canonical_row_i)))
///   fold_0       = 64 ASCII '0'
///   fold_i       = lowercase_hex(SHA256(utf8(fold_{i-1} || row_hash_i)))
///   table_hash   = fold_n, rows in primary-key order
///   row_hash_sum = sum of the first 15 hex chars (60 bits) of each row hash
/// </summary>
internal sealed class Checksum
{
    public const string Seed = "0000000000000000000000000000000000000000000000000000000000000000";

    private string _fold = Seed;
    private BigInteger _rowHashSum = BigInteger.Zero;
    private long _rowCount;

    public void Add(string canonicalRow)
    {
        var rowHash = Sha256Hex(canonicalRow);
        _rowCount++;
        _rowHashSum += BigInteger.Parse("0" + rowHash[..15], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        _fold = Sha256Hex(_fold + rowHash);
    }

    public long RowCount => _rowCount;

    public string TableHash => _fold;

    public string RowHashSum => _rowHashSum.ToString(CultureInfo.InvariantCulture);

    private static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
