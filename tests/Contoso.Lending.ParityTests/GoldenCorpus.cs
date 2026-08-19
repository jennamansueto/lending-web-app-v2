using System.Collections.Concurrent;
using System.Text.Json;

namespace Contoso.Lending.ParityTests;

/// <summary>
/// Loads the golden corpus (parity/golden/*.json) copied verbatim from the
/// legacy repo's parity branch. Records are addressed by (file, index) so each
/// xUnit theory case is serializable and reported individually per rule.
/// </summary>
public static class GoldenCorpus
{
    public const int ExpectedTotalRecords = 688;
    public const int ExpectedFileCount = 24;

    private static readonly Lazy<string> GoldenDir = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "parity", "golden");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("parity/golden not found above " + AppContext.BaseDirectory);
    });

    private static readonly ConcurrentDictionary<string, JsonElement[]> Cache = new();

    public static string Dir => GoldenDir.Value;

    public static string[] AllFiles() =>
        Directory.GetFiles(Dir, "*.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToArray()!;

    public static JsonElement[] Load(string fileName) => Cache.GetOrAdd(fileName, f =>
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, f)));
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    });

    public static JsonElement Record(string fileName, int index) => Load(fileName)[index];

    public static TheoryData<int, string> Cases(string fileName)
    {
        var data = new TheoryData<int, string>();
        var records = Load(fileName);
        for (int i = 0; i < records.Length; i++)
        {
            string description = records[i].TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()!
                : records[i].GetProperty("input").GetRawText();
            data.Add(i, description);
        }
        return data;
    }

    public static decimal? GetDecimal(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null) return null;
        return el.GetDecimal();
    }

    public static string? GetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el) || el.ValueKind == JsonValueKind.Null) return null;
        return el.GetString();
    }

    public static int GetInt(JsonElement obj, string name) => obj.GetProperty(name).GetInt32();
}
