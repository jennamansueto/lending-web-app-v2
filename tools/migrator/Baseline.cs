using System.Text.Json;

namespace Contoso.Lending.Migrator;

/// <summary>
/// The committed Phase-1 Oracle baseline (<c>database/checks/baseline-oracle.json</c>):
/// row counts, canonical numeric sums, hash chains and sequence positions as they
/// stood in the legacy system of record.
/// </summary>
internal sealed class Baseline
{
    private Baseline(
        string specVersion,
        Dictionary<string, BaselineTable> tables,
        Dictionary<string, long> sequences)
    {
        SpecVersion = specVersion;
        Tables = tables;
        Sequences = sequences;
    }

    public string SpecVersion { get; }

    public Dictionary<string, BaselineTable> Tables { get; }

    public Dictionary<string, long> Sequences { get; }

    public static Baseline Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;

        var tables = new Dictionary<string, BaselineTable>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in root.GetProperty("tables").EnumerateArray())
        {
            var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sum in element.GetProperty("numeric_sums").EnumerateObject())
            {
                sums[sum.Name] = sum.Value.GetString()!;
            }

            var name = element.GetProperty("table").GetString()!;
            tables[name] = new BaselineTable(
                name,
                element.GetProperty("row_count").GetInt64(),
                sums,
                element.GetProperty("row_hash_chain").GetString()!,
                element.GetProperty("delimiter_collisions").GetInt64());
        }

        var sequences = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in root.GetProperty("sequences").EnumerateArray())
        {
            sequences[element.GetProperty("name").GetString()!] =
                element.GetProperty("last_number").GetInt64();
        }

        return new Baseline(root.GetProperty("spec_version").GetString()!, tables, sequences);
    }
}

internal sealed record BaselineTable(
    string Table,
    long RowCount,
    Dictionary<string, string> NumericSums,
    string RowHashChain,
    long DelimiterCollisions);
