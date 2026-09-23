using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Contoso.Lending.ParityTests;

/// <summary>
/// Loads `parity/golden/*.json`, the behavioral contract captured from the legacy desk.
/// Records are exposed as raw <see cref="JsonElement"/> so numbers can be read as
/// <see cref="decimal"/> — never through double.
/// </summary>
public static class GoldenCorpus
{
    /// <summary>Rule ID -> golden file name. The corpus and this map must reconcile exactly.</summary>
    public static readonly IReadOnlyDictionary<string, string> RuleFiles = new Dictionary<string, string>
    {
        ["BR-ELG-001"] = "BR-ELG-001_amount_minimum.json",
        ["BR-ELG-002"] = "BR-ELG-002_amount_maximum.json",
        ["BR-ELG-003"] = "BR-ELG-003_term_limits.json",
        ["BR-ELG-004"] = "BR-ELG-004_minimum_credit_score.json",
        ["BR-ELG-005"] = "BR-ELG-005_dti_cap.json",
        ["BR-ELG-006"] = "BR-ELG-006_collateral_required.json",
        ["BR-ELG-007"] = "BR-ELG-007_ltv_cap.json",
        ["BR-ELG-008"] = "BR-ELG-008_years_in_business.json",
        ["BR-ELG-009"] = "BR-ELG-009_approval_result.json",
        ["BR-PQL-001"] = "BR-PQL-001_prequalification_hint.json",
        ["BR-PRC-001"] = "BR-PRC-001_base_rate.json",
        ["BR-PRC-002"] = "BR-PRC-002_risk_spread.json",
        ["BR-PRC-003"] = "BR-PRC-003_ltv_adjustment.json",
        ["BR-PRC-004"] = "BR-PRC-004_relationship_discount.json",
        ["BR-PRC-005"] = "BR-PRC-005_priced_rate.json",
        ["BR-PRC-006"] = "BR-PRC-006_origination_fee.json",
        ["BR-AMT-001"] = "BR-AMT-001_monthly_payment.json",
        ["BR-AMT-002"] = "BR-AMT-002_amortization_schedule.json",
        ["BR-SVC-001"] = "BR-SVC-001_late_fee.json",
        ["BR-SVC-002"] = "BR-SVC-002_payoff_quote.json"
    };

    public static string GoldenDirectory { get; } = FindGoldenDirectory();

    private static readonly Dictionary<string, IReadOnlyList<JsonElement>> Cache = new();

    public static IReadOnlyList<JsonElement> Records(string ruleId)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(ruleId, out var cached)) return cached;
            string path = Path.Combine(GoldenDirectory, RuleFiles[ruleId]);
            using var stream = File.OpenRead(path);
            var document = JsonDocument.Parse(stream);
            var records = document.RootElement.EnumerateArray().ToList();
            Cache[ruleId] = records;
            return records;
        }
    }

    public static IEnumerable<string> GoldenFileNames() =>
        Directory.EnumerateFiles(GoldenDirectory, "*.json").Select(Path.GetFileName)!;

    private static string FindGoldenDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "parity", "golden");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("parity/golden not found above " + AppContext.BaseDirectory);
    }
}
