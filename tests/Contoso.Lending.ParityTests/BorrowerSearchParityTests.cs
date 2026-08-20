using Contoso.Lending.Domain;

namespace Contoso.Lending.ParityTests;

[Collection(ParityArtifactCollection.Name)]
public class BorrowerSearchParityTests
{
    private const string Pql002 = "BR-PQL-002_borrower_search.json";

    // Test-only fixture mirroring the legacy seed data
    // (lending-desktop-app/database/seed/seed_data.sql:3-10, loans at :20-24)
    // the corpus was executed against. Loans exist for borrowers 1, 2, 3, 5, 7;
    // ACTIVE_LOANS counts every loan row regardless of status (BR-PQL-002 quirk).
    private static readonly (int Id, string LegalName, string TaxId, int CreditScore, decimal DepositBalance, int YearsInBusiness, int ActiveLoans)[] SeedBorrowers =
    [
        (1, "ACME INDUSTRIAL SUPPLY LLC", "84-1234567", 742, 310000.00m, 12, 1),
        (2, "BLUE HARBOR SEAFOOD CO", "84-2345678", 688, 145000.00m, 7, 1),
        (3, "CASCADE PRECISION MACHINING", "84-3456789", 731, 82000.00m, 15, 1),
        (4, "DELTA LOGISTICS PARTNERS", "84-4567890", 655, 21000.00m, 3, 0),
        (5, "EVERGREEN DENTAL GROUP", "84-5678901", 778, 505000.00m, 9, 1),
        (6, "FOUNDRY COFFEE ROASTERS", "84-6789012", 631, 12000.00m, 4, 0),
        (7, "GRANITE PEAK CONSTRUCTION", "84-7890123", 702, 96000.00m, 18, 1),
        (8, "HARBORLIGHT MARINE SERVICES", "84-8901234", 664, 54000.00m, 6, 0),
    ];

    public static TheoryData<int, string> Cases_PQL_002 => GoldenCorpus.Cases(Pql002);

    [Theory]
    [MemberData(nameof(Cases_PQL_002))]
    public void BR_PQL_002(int index, string description)
    {
        _ = description;
        var record = GoldenCorpus.Record(Pql002, index);
        var expected = record.GetProperty("expected");
        string searchText = record.GetProperty("input").GetProperty("searchText").GetString()!;

        string boundTerm = BorrowerSearchSemantics.BuildBoundTerm(searchText);
        Assert.Equal(GoldenCorpus.GetString(expected, "boundTerm"), boundTerm);

        var matched = SeedBorrowers
            .Where(b => BorrowerSearchSemantics.Matches(boundTerm, b.LegalName, b.TaxId))
            .OrderBy(b => b.LegalName, StringComparer.Ordinal)
            .ToList();

        ParityArtifact.Complete(Pql002, index, new
        {
            boundTerm,
            rowCount = matched.Count,
            rows = matched.Select(b => new
            {
                borrowerId = b.Id,
                legalName = b.LegalName,
                taxId = b.TaxId,
                creditScore = b.CreditScore,
                depositBalance = b.DepositBalance,
                yearsInBusiness = b.YearsInBusiness,
                activeLoans = b.ActiveLoans,
            }).ToArray(),
        });

        Assert.Equal(GoldenCorpus.GetInt(expected, "rowCount"), matched.Count);

        var expectedRows = expected.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(expectedRows.Length, matched.Count);
        for (int r = 0; r < expectedRows.Length; r++)
        {
            Assert.Equal(GoldenCorpus.GetInt(expectedRows[r], "borrowerId"), matched[r].Id);
            Assert.Equal(GoldenCorpus.GetString(expectedRows[r], "legalName"), matched[r].LegalName);
            Assert.Equal(GoldenCorpus.GetString(expectedRows[r], "taxId"), matched[r].TaxId);
            Assert.Equal(GoldenCorpus.GetInt(expectedRows[r], "creditScore"), matched[r].CreditScore);
            Assert.Equal(expectedRows[r].GetProperty("depositBalance").GetDecimal(), matched[r].DepositBalance);
            Assert.Equal(GoldenCorpus.GetInt(expectedRows[r], "yearsInBusiness"), matched[r].YearsInBusiness);
            Assert.Equal(GoldenCorpus.GetInt(expectedRows[r], "activeLoans"), matched[r].ActiveLoans);
        }
    }
}
