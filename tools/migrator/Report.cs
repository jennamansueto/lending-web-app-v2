using System.Text.Json;

namespace Contoso.Lending.Migrator;

internal sealed record CheckResult(string Table, string Check, string Oracle, string Postgres, bool Pass);

internal static class Report
{
    /// <summary>
    /// Writes <c>parity/reports/data-parity.json</c> under the repository root
    /// (override with <c>PARITY_REPORT</c>) and returns the path written.
    /// The content is derived from the data only — no timestamps or durations —
    /// so repeated runs on identical data produce an identical file.
    /// </summary>
    public static string Write(IReadOnlyList<CheckResult> results)
    {
        var path = Environment.GetEnvironmentVariable("PARITY_REPORT") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(RepositoryRoot(), "parity", "reports", "data-parity.json");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var payload = new
        {
            algorithm_version = 1,
            source = "oracle",
            target = "postgres",
            tolerance = "zero",
            passed = results.All(r => r.Pass),
            checks_total = results.Count,
            checks_failed = results.Count(r => !r.Pass),
            checks = results.Select(r => new
            {
                table = r.Table,
                check = r.Check,
                oracle = r.Oracle,
                postgres = r.Postgres,
                pass = r.Pass,
            }),
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return path;
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                Directory.Exists(Path.Combine(dir.FullName, "database", "postgres")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
