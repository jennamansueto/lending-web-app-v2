using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Contoso.Lending.Domain;
using Xunit;

namespace Contoso.Lending.ParityTests;

/// <summary>
/// The corpus and the engines must reconcile with zero unexplained items: no golden file
/// without a test mapping, no mapped rule without a golden file, no empty or mislabelled file.
/// </summary>
public class CorpusReconciliationTests
{
    /// <summary>Every rule ID the domain implements that the corpus is expected to cover.</summary>
    private static readonly string[] ImplementedRules =
    {
        "BR-ELG-001", "BR-ELG-002", "BR-ELG-003", "BR-ELG-004", "BR-ELG-005", "BR-ELG-006",
        "BR-ELG-007", "BR-ELG-008", "BR-ELG-009", "BR-PQL-001",
        "BR-PRC-001", "BR-PRC-002", "BR-PRC-003", "BR-PRC-004", "BR-PRC-005", "BR-PRC-006",
        "BR-AMT-001", "BR-AMT-002", "BR-SVC-001", "BR-SVC-002"
    };

    [Fact]
    public void Every_golden_file_has_a_test_mapping()
    {
        var mapped = GoldenCorpus.RuleFiles.Values.ToHashSet(StringComparer.Ordinal);
        var onDisk = GoldenCorpus.GoldenFileNames().ToHashSet(StringComparer.Ordinal);
        Assert.Empty(onDisk.Except(mapped));
    }

    [Fact]
    public void Every_mapped_rule_has_a_golden_file()
    {
        var onDisk = GoldenCorpus.GoldenFileNames().ToHashSet(StringComparer.Ordinal);
        Assert.Empty(GoldenCorpus.RuleFiles.Values.Except(onDisk));
    }

    [Fact]
    public void Every_implemented_rule_is_covered_by_the_corpus()
    {
        Assert.Empty(ImplementedRules.Except(GoldenCorpus.RuleFiles.Keys));
        Assert.Empty(GoldenCorpus.RuleFiles.Keys.Except(ImplementedRules));
    }

    [Fact]
    public void Every_golden_file_is_non_empty_and_self_labelled()
    {
        foreach (var (ruleId, file) in GoldenCorpus.RuleFiles)
        {
            var records = GoldenCorpus.Records(ruleId);
            Assert.True(records.Count > 0, file + " contains no records");
            Assert.All(records, record => Assert.Equal(ruleId, record.GetProperty("ruleId").GetString()));
            Assert.All(records, record =>
            {
                Assert.True(record.TryGetProperty("input", out _), file + " record without input");
                Assert.True(record.TryGetProperty("expected", out _), file + " record without expected");
            });
        }
    }

    [Fact]
    public void Corpus_size_is_pinned()
    {
        var counts = GoldenCorpus.RuleFiles.Keys.ToDictionary(id => id, id => GoldenCorpus.Records(id).Count);
        Assert.Equal(20, counts.Count);
        Assert.Equal(772, counts.Values.Sum());
    }

    // §10.3 of the legacy rule catalog: the two service-layer exception contracts that no
    // golden record exercises.
    [Fact]
    public void Unknown_product_type_throws_the_legacy_message()
    {
        Assert.Equal("Unknown product type: FOO",
            Assert.Throws<ArgumentException>(() => PricingEngine.GetBaseRate("FOO")).Message);
        Assert.Equal("Unknown product type: FOO",
            Assert.Throws<ArgumentException>(() => PricingEngine.CalcOriginationFee(1000m, "FOO")).Message);
    }

    [Fact]
    public void Non_positive_term_throws_the_legacy_message()
    {
        Assert.Equal("termMonths must be positive",
            Assert.Throws<ArgumentException>(() => AmortizationEngine.MonthlyPayment(1000m, 6.5m, 0)).Message);
        Assert.Equal("termMonths must be positive",
            Assert.Throws<ArgumentException>(() => AmortizationEngine.MonthlyPayment(1000m, 6.5m, -12)).Message);
    }

    [Fact]
    public void Result_strings_do_not_depend_on_the_host_locale()
    {
        var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            var result = EligibilityEngine.Evaluate(new EligibilityRequest("TERM", 100000m, 60, 1200000m, 0m, 800, 1000000m, 10));
            Assert.Equal(
                "APPROVED FOR UNDERWRITING\nDTI: 0.020   LTV: 0.100\nEst. payment at base rate: $1,956.61",
                result.ResultText);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = previous;
        }
    }
}
