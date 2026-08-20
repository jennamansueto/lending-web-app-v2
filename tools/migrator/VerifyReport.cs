using System.Text.Json;

namespace Contoso.Lending.Migrator;

internal sealed record VerifyReport(IReadOnlyList<VerifyRow> Rows)
{
    public string SpecVersion => "1.0";

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }) + Environment.NewLine);
    }
}

internal sealed record VerifyRow(
    string Table,
    string Metric,
    string Oracle,
    string Postgres,
    string Status,
    string? Baseline = null);
