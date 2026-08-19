using System.Text.Json;

namespace Contoso.Lending.ParityTests;

public class CorpusIntegrityTests
{
    // The gate: every golden file loads, and the total record count is exactly
    // 688 so silent under-collection fails the suite.
    [Fact]
    public void Corpus_has_24_files_and_688_records()
    {
        var files = GoldenCorpus.AllFiles();
        Assert.Equal(GoldenCorpus.ExpectedFileCount, files.Length);
        int total = files.Sum(f => GoldenCorpus.Load(f).Length);
        Assert.Equal(GoldenCorpus.ExpectedTotalRecords, total);
    }

    [Fact]
    public void Every_record_has_ruleId_and_expected_or_expectedError()
    {
        foreach (var file in GoldenCorpus.AllFiles())
        {
            foreach (var record in GoldenCorpus.Load(file))
            {
                Assert.True(record.TryGetProperty("ruleId", out var ruleId) && ruleId.ValueKind == JsonValueKind.String,
                    file + ": record missing ruleId");
                bool hasExpected = record.TryGetProperty("expected", out _);
                bool hasError = record.TryGetProperty("expectedError", out _);
                Assert.True(hasExpected || hasError, file + ": record has neither expected nor expectedError");
            }
        }
    }
}
