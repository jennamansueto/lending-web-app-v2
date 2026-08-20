using Contoso.Lending.Domain;

namespace Contoso.Lending.ParityTests;

[Collection(ParityArtifactCollection.Name)]
public class PersistenceParityTests
{
    private const string Elg010 = "BR-ELG-010_approved_application_persistence.json";

    public static TheoryData<int, string> Cases_ELG_010 => GoldenCorpus.Cases(Elg010);

    [Theory]
    [MemberData(nameof(Cases_ELG_010))]
    public void BR_ELG_010(int index, string description)
    {
        _ = description;
        var record = GoldenCorpus.Record(Elg010, index);
        var input = record.GetProperty("input");
        var expected = record.GetProperty("expected");

        var result = EligibilityEngine.Evaluate(EligibilityParityTests.ReadInput(input));
        Assert.Equal("APPROVED", result.Decision);

        var persisted = ApplicationPersistence.Build(
            GoldenCorpus.GetInt(input, "borrowerId"),
            input.GetProperty("product").GetString()!,
            input.GetProperty("amount").GetDecimal(),
            GoldenCorpus.GetInt(input, "termMonths"),
            GoldenCorpus.GetInt(input, "creditScore"),
            result.Dti!.Value,
            result.Ltv!.Value);

        var contrast = record.GetProperty("roundingContrast");
        ParityArtifact.Complete(Elg010, index, new
        {
            borrowerId = persisted.BorrowerId,
            productType = persisted.ProductType,
            amount = persisted.Amount,
            termMonths = persisted.TermMonths,
            creditScore = persisted.CreditScore,
            dti = persisted.Dti,
            ltv = persisted.Ltv,
            status = persisted.Status,
            appIdSource = ApplicationPersistence.AppIdSource,
            createdAtSource = ApplicationPersistence.CreatedAtSource,
            roundingContrast = new
            {
                unroundedDti = result.Dti,
                unroundedLtv = result.Ltv,
                dtiAwayFromZero = Math.Round(result.Dti!.Value, 4, MidpointRounding.AwayFromZero),
                ltvAwayFromZero = Math.Round(result.Ltv!.Value, 4, MidpointRounding.AwayFromZero),
                dtiRoundingDiffers = persisted.Dti != Math.Round(result.Dti.Value, 4, MidpointRounding.AwayFromZero),
                ltvRoundingDiffers = persisted.Ltv != Math.Round(result.Ltv.Value, 4, MidpointRounding.AwayFromZero),
            },
        });

        Assert.Equal(GoldenCorpus.GetInt(expected, "borrowerId"), persisted.BorrowerId);
        Assert.Equal(GoldenCorpus.GetString(expected, "productType"), persisted.ProductType);
        Assert.Equal(GoldenCorpus.GetDecimal(expected, "amount"), persisted.Amount);
        Assert.Equal(GoldenCorpus.GetInt(expected, "termMonths"), persisted.TermMonths);
        Assert.Equal(GoldenCorpus.GetInt(expected, "creditScore"), persisted.CreditScore);
        Assert.Equal(GoldenCorpus.GetDecimal(expected, "dti"), persisted.Dti);
        Assert.Equal(GoldenCorpus.GetDecimal(expected, "ltv"), persisted.Ltv);
        Assert.Equal(GoldenCorpus.GetString(expected, "status"), persisted.Status);
        Assert.Equal(ApplicationPersistence.AppIdSource, GoldenCorpus.GetString(expected, "appIdSource"));
        Assert.Equal(ApplicationPersistence.CreatedAtSource, GoldenCorpus.GetString(expected, "createdAtSource"));

        // The corpus records the ToEven-vs-AwayFromZero contrast (BR-ELG-010):
        // verify the persisted values used legacy banker's rounding.
        Assert.Equal(GoldenCorpus.GetDecimal(contrast, "unroundedDti"), result.Dti);
        Assert.Equal(GoldenCorpus.GetDecimal(contrast, "unroundedLtv"), result.Ltv);
        Assert.Equal(GoldenCorpus.GetDecimal(contrast, "dtiAwayFromZero"), Math.Round(result.Dti!.Value, 4, MidpointRounding.AwayFromZero));
        Assert.Equal(GoldenCorpus.GetDecimal(contrast, "ltvAwayFromZero"), Math.Round(result.Ltv!.Value, 4, MidpointRounding.AwayFromZero));
        Assert.Equal(contrast.GetProperty("dtiRoundingDiffers").GetBoolean(),
            persisted.Dti != Math.Round(result.Dti!.Value, 4, MidpointRounding.AwayFromZero));
        Assert.Equal(contrast.GetProperty("ltvRoundingDiffers").GetBoolean(),
            persisted.Ltv != Math.Round(result.Ltv!.Value, 4, MidpointRounding.AwayFromZero));
    }
}
